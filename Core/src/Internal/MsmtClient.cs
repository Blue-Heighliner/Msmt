namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// Sends MSMT messages to a single remote server, queuing send requests so calling code never blocks on
/// the underlying TLS connection. Used internally by <see cref="MsmtPeer"/> to back each outgoing
/// connection it creates on demand.
/// </summary>
/// <remarks>
/// Queued sends are processed one at a time, in priority order and first-in-first-out within a priority,
/// by a single background loop that also owns the underlying connection. A second background loop queues a
/// maintenance check once a second while a connection is open, run by the processing loop so it never races
/// a send: it closes a <see cref="MsmtOperationMode.Session"/> connection whose negotiated lifetime has
/// ended, notices a connection the remote peer closed while idle, and sends a reachability check as a
/// keep-alive once a Session connection has been silent for its randomized interval, per the ICD.
/// Neither BouncyCastle's blocking handshake nor its TLS stream honors a cancellation token once blocked,
/// so a send's cancellation token, <see cref="Cancel"/> against its tag, disposal, and the handshake,
/// stall and response timeouts all work by forcing the underlying socket closed (see <see
/// cref="MsmtWatchdog"/>), promptly interrupting whatever was in flight.
/// </remarks>
internal sealed class MsmtClient(IMsmtPackageTracker? packageTracker = null) : IMsmtLink
{
    private readonly IMsmtPackageTracker tracker = packageTracker ?? new MsmtPackageTracker();
    private readonly PriorityQueue<PendingSend, (int NegatedPriority, long Sequence)> queue = new();
    private readonly Lock queueLock = new();

    // Neither this nor disposalCancellation is ever disposed, and neither holds anything that needs releasing:
    // a racing Enqueue may still release this, and background work may still read disposalCancellation's
    // Token, which throws once disposed, after shutdown.
    private readonly SemaphoreSlim queueSignal = new(0);
    private readonly CancellationTokenSource disposalCancellation = new();
    private readonly Random keepAliveRandom = new();
    private readonly TimeSpan maintenanceInterval = TimeSpan.FromSeconds(1);
    private readonly MsmtNameTarget unconnectedTarget = new() { Host = string.Empty, Port = 0, ServerName = string.Empty };

    private MsmtConnectOptions options = null!;
    private Action? removeFromCache;
    private long sendSequence;
    private Task? processingLoop;
    private Task? maintenanceLoop;
    private TcpClient? tcpClient;
    private MsmtWatchdog? watchdog;
    private RekeyableTlsClientProtocol? connection;
    private MsmtIdentity? remoteIdentity;
    private DateTime sessionExpiresAtUtc;
    private long lastActivityTicks = DateTime.UtcNow.Ticks;
    private long lastWireActivityTicks = DateTime.UtcNow.Ticks;
    private int outstandingSends;
    private int maintenanceQueued;
    private TimeSpan keepAliveInterval;
    private int messagesSinceConnect;
    private bool isDisposed;

    /// <summary>Raised whenever this client begins attempting to establish a new outgoing connection to its remote server.</summary>
    public event EventHandler<MsmtLinkingEventArgs> Linking = delegate { };

    /// <summary>Raised whenever this client establishes a new outgoing connection to its remote server, once its handshake has fully completed and been secured.</summary>
    public event EventHandler<MsmtLinkedEventArgs> Linked = delegate { };

    /// <summary>Raised whenever an attempted outgoing connection to the remote server fails before completing its handshake.</summary>
    public event EventHandler<MsmtLinkFailedEventArgs> LinkFailed = delegate { };

    /// <summary>Raised whenever an established outgoing connection to the server closes, whether by normal teardown or an unexpected failure.</summary>
    public event EventHandler<MsmtUnlinkedEventArgs> Unlinked = delegate { };

    /// <summary>Raised as a send's progress changes, for sends given a non-<see langword="null"/> tag.</summary>
    public event EventHandler<MsmtPackageChangedEventArgs> PackageChanged = delegate { };

    /// <summary>Gets a value indicating whether this client's underlying TLS connection is currently open.</summary>
    internal bool IsConnected => connection is not null;

    /// <summary>
    /// Gets a value indicating whether this client currently has no send queued or in flight, and no <see
    /// cref="Reserve"/> outstanding. Used by <see cref="MsmtOptions.MaxConnectionCount"/> eviction to never
    /// interrupt a send/request-response cycle in progress, or one about to be queued.
    /// </summary>
    internal bool IsIdle => Volatile.Read(ref outstandingSends) == 0;

    /// <summary>Gets the last time an application send completed on this client, not counting keep-alives or session negotiation.</summary>
    internal DateTime LastActivityUtc => new(Interlocked.Read(ref lastActivityTicks), DateTimeKind.Utc);

    /// <summary>Gets the last time any message, including a keep-alive or session negotiation, completed on this client's connection. Exposed for diagnostics and testing.</summary>
    internal DateTime LastWireActivityUtc => new(Interlocked.Read(ref lastWireActivityTicks), DateTimeKind.Utc);

    /// <summary>Gets the remote server this client sends to, used as every one of its packages' <see cref="IMsmtPackage.Target"/>.</summary>
    internal MsmtNameTarget Target => options.Target;

    /// <summary>Gets this client's underlying socket, or <see langword="null"/> if not currently connected. Exposed for diagnostics and testing (e.g. verifying a <see cref="MsmtSendOptions.Dscp"/> marking was applied).</summary>
    internal Socket? Socket => tcpClient?.Client;

    /// <inheritdoc />
    public MsmtLinkType Kind => MsmtLinkType.Sender;

    /// <inheritdoc />
    public MsmtIdentity? Identity => remoteIdentity;

    /// <inheritdoc />
    public IMsmtConnection Connection { get; private set; } = null!;

    /// <summary>
    /// Starts this client's background send-processing loop, transitioning it from its initial unstarted
    /// state to a started state ready to accept sends.
    /// </summary>
    /// <param name="options">The remote server and credentials this client sends to.</param>
    /// <returns>A task that completes once the client is ready to send.</returns>
    public Task Connect(MsmtConnectOptions options)
    {
        this.options = options;
        processingLoop ??= Task.Run(ProcessQueueLoop);
        maintenanceLoop ??= Task.Run(MaintenanceLoop);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Attaches the callback this client invokes to remove itself from its owning peer's on-demand
    /// connection cache once <see cref="Drop"/> or <see cref="Disconnect"/> is called.
    /// </summary>
    /// <param name="removeFromCache">Removes this client's entry from the owning peer's cache.</param>
    internal void AttachToCache(Action removeFromCache) => this.removeFromCache = removeFromCache;

    /// <summary>Attaches the connection this client belongs to.</summary>
    /// <param name="connection">This client's owning connection.</param>
    internal void AttachConnection(IMsmtConnection connection) => Connection = connection;

    /// <summary>
    /// Holds this client non-idle (see <see cref="IsIdle"/>) until the matching <see
    /// cref="ReleaseReservation"/>, so eviction can't dispose it between a caller looking it up and queuing
    /// a send on it.
    /// </summary>
    internal void Reserve() => Interlocked.Increment(ref outstandingSends);

    /// <summary>Releases a hold taken by <see cref="Reserve"/>.</summary>
    internal void ReleaseReservation() => Interlocked.Decrement(ref outstandingSends);

    /// <summary>Immediately abandons this client's cache entry and closes its connection without waiting.</summary>
    public void Drop()
    {
        removeFromCache?.Invoke();
        Dispose();
    }

    /// <summary>Cleanly removes this client's cache entry and shuts its connection down, awaiting its background loops first.</summary>
    /// <returns>A task that completes once the shutdown has finished.</returns>
    public async Task Disconnect()
    {
        removeFromCache?.Invoke();
        await DisposeAsync();
    }

    /// <summary>
    /// Queues a message for delivery to the remote server, without requesting or waiting for a response.
    /// This method only enqueues the payload; it does not wait for the send to complete. If this method
    /// returns without throwing, ownership of <paramref name="payload"/> transfers to this client, which
    /// disposes it once the send completes, successfully or not; if it throws, the caller retains
    /// ownership.
    /// </summary>
    /// <param name="payload">The application message content, rented from a pool.</param>
    /// <param name="options">Options governing how this payload is sent, or <see langword="null"/> to use the defaults.</param>
    /// <param name="cancellation">Cancels the queued send before it is processed.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
    /// <exception cref="ObjectDisposedException">This client has been disposed.</exception>
    public void Send(IMemoryOwner<byte> payload, MsmtSendOptions? options = null, CancellationToken cancellation = default)
    {
        MsmtProtocol.ValidatePayloadLength(payload.Memory.Length, nameof(payload));
        MsmtSendOptions sendOptions = options ?? new MsmtSendOptions();
        bool isQueued = Enqueue(payload, MsmtMessageFlags.None, sendOptions.Tag, sendOptions.Priority, sendOptions.Dscp, cancellation, null);
        ObjectDisposedException.ThrowIf(!isQueued, this);
    }

    /// <summary>
    /// Queues a message for delivery to the remote server, wrapping <paramref name="payload"/> in a
    /// non-pooled <see cref="IMemoryOwner{T}"/> so callers with an ordinary <see cref="ReadOnlyMemory{T}"/>
    /// don't need to manage one themselves.
    /// </summary>
    /// <param name="payload">The application message content. Not copied - the caller must not mutate it until the send completes.</param>
    /// <param name="options">Options governing how this payload is sent, or <see langword="null"/> to use the defaults.</param>
    /// <param name="cancellation">Cancels the queued send before it is processed.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
    /// <exception cref="ObjectDisposedException">This client has been disposed.</exception>
    public void Send(ReadOnlyMemory<byte> payload, MsmtSendOptions? options = null, CancellationToken cancellation = default) =>
        Send(new NonOwningMemoryOwner(payload), options, cancellation);

    /// <summary>
    /// Queues a message for delivery to the remote server and asynchronously awaits its acknowledgement.
    /// If this method returns without throwing, ownership of <paramref name="payload"/> transfers to this
    /// client, which disposes it once the request completes, successfully or not; if it throws, the caller
    /// retains ownership.
    /// </summary>
    /// <param name="payload">The application message content, rented from a pool.</param>
    /// <param name="options">Options governing how this payload is sent, or <see langword="null"/> to use the defaults.</param>
    /// <param name="cancellation">Cancels the request before it completes.</param>
    /// <returns>The remote server's acknowledgement.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
    /// <exception cref="ObjectDisposedException">This client has been disposed.</exception>
    public Task<MsmtResponse> Request(IMemoryOwner<byte> payload, MsmtSendOptions? options = null, CancellationToken cancellation = default)
    {
        MsmtProtocol.ValidatePayloadLength(payload.Memory.Length, nameof(payload));
        MsmtSendOptions sendOptions = options ?? new MsmtSendOptions();
        TaskCompletionSource<MsmtResponse> responseSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool isQueued = Enqueue(payload, MsmtMessageFlags.AcknowledgementRequestedOrGiven, sendOptions.Tag, sendOptions.Priority, sendOptions.Dscp, cancellation, responseSource);
        ObjectDisposedException.ThrowIf(!isQueued, this);
        return responseSource.Task;
    }

    /// <summary>
    /// Queues a message for delivery to the remote server and asynchronously awaits its acknowledgement,
    /// wrapping <paramref name="payload"/> in a non-pooled <see cref="IMemoryOwner{T}"/> so callers with an
    /// ordinary <see cref="ReadOnlyMemory{T}"/> don't need to manage one themselves.
    /// </summary>
    /// <param name="payload">The application message content. Not copied - the caller must not mutate it until the request completes.</param>
    /// <param name="options">Options governing how this payload is sent, or <see langword="null"/> to use the defaults.</param>
    /// <param name="cancellation">Cancels the request before it completes.</param>
    /// <returns>The remote server's acknowledgement.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
    /// <exception cref="ObjectDisposedException">This client has been disposed.</exception>
    public Task<MsmtResponse> Request(ReadOnlyMemory<byte> payload, MsmtSendOptions? options = null, CancellationToken cancellation = default) =>
        Request(new NonOwningMemoryOwner(payload), options, cancellation);

    /// <summary>
    /// Requests cancellation of the still-outstanding tagged send previously queued via <see
    /// cref="Send(IMemoryOwner{byte}, MsmtSendOptions?, CancellationToken)"/> with <paramref name="tag"/>.
    /// Does nothing if no send is currently outstanding for that tag.
    /// </summary>
    /// <param name="tag">The tag identifying the send to cancel, as passed to <see cref="Send(IMemoryOwner{byte}, MsmtSendOptions?, CancellationToken)"/>.</param>
    public void Cancel(object tag) => tracker.Cancel(tag);

    /// <summary>
    /// Gets the current status of the tagged send previously queued via <see cref="Send(IMemoryOwner{byte},
    /// MsmtSendOptions?, CancellationToken)"/> with <paramref name="tag"/>. A completed or cancelled tag's
    /// status is forgotten five minutes after the send finished, so this may also return <see
    /// langword="null"/> for a tag whose send finished longer ago than that.
    /// </summary>
    /// <param name="tag">The tag identifying the send to look up, as passed to <see cref="Send(IMemoryOwner{byte}, MsmtSendOptions?, CancellationToken)"/>.</param>
    /// <returns>The send's current status, or <see langword="null"/> if no send was ever queued with this tag, or its status has since been forgotten.</returns>
    public MsmtSendStatus? GetStatus(object tag) => tracker.GetStatus(tag);

    /// <summary>
    /// Immediately abandons this client: cancels its background loops and closes its connection without
    /// waiting for either to finish. Prefer <see cref="DisposeAsync"/> for a clean, awaited shutdown; use
    /// this only when an immediate, non-blocking teardown is required.
    /// </summary>
    public void Dispose()
    {
        if (!MarkDisposed())
        {
            return;
        }

        disposalCancellation.Cancel();
        DrainQueueOnShutdown();
        CloseConnection();
    }

    /// <summary>
    /// Cleanly shuts this client down: cancels its background loops, forces the underlying socket closed so
    /// a blocked acknowledgement read can never stall this method, awaits both loops' completion, then
    /// finishes closing the connection. Prefer this over <see cref="Dispose"/> whenever an awaited shutdown
    /// is acceptable.
    /// </summary>
    /// <returns>A task that completes once the shutdown has finished.</returns>
    public async ValueTask DisposeAsync()
    {
        if (!MarkDisposed())
        {
            return;
        }

        await disposalCancellation.CancelAsync();

        // BouncyCastle's TLS stream falls back to a blocking byte[]-based ReadAsync overload it never
        // overrides internally (see MsmtServer.DisposeAsync), so a cancelled token alone cannot interrupt a
        // read already in progress inside SendOverConnection - awaiting processingLoop below would hang
        // forever without this. Only the raw socket is forced closed here, not the full CloseConnection -
        // ProcessQueueLoop is still the sole owner of the connection field and its own Unlinked bookkeeping,
        // which it performs itself once the resulting exception unwinds SendOverConnection.
        ForceCloseSocket();

        if (processingLoop is not null)
        {
            try
            {
                await processingLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (maintenanceLoop is not null)
        {
            try
            {
                await maintenanceLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        DrainQueueOnShutdown();
        CloseConnection();
    }

    /// <summary>
    /// Marks this client disposed under the queue lock, so every <see cref="Enqueue"/> either lands before
    /// this (and is drained by shutdown) or is refused.
    /// </summary>
    /// <returns><see langword="true"/> if this call marked it; <see langword="false"/> if it was already disposed.</returns>
    private bool MarkDisposed()
    {
        lock (queueLock)
        {
            if (isDisposed)
            {
                return false;
            }

            isDisposed = true;
            return true;
        }
    }

    /// <summary>Immediately closes the underlying socket, if any, without touching <see cref="MsmtClient"/>'s own connection bookkeeping.</summary>
    private void ForceCloseSocket() => CloseSocket(tcpClient);

    private void CloseSocket(TcpClient? client)
    {
        try
        {
            client?.Close();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }

    private bool Enqueue(IMemoryOwner<byte> payload, MsmtMessageFlags flags, object? tag, int priority, int? dscp, CancellationToken cancellation, TaskCompletionSource<MsmtResponse>? responseSource, PendingKind kind = PendingKind.Message)
    {
        PendingSend item = new()
        {
            Payload = payload,
            Flags = flags,
            Tag = tag,
            Dscp = dscp,
            Cancellation = cancellation,
            ResponseSource = responseSource,
            Kind = kind,
        };

        lock (queueLock)
        {
            if (isDisposed)
            {
                item.CancelSource.Dispose();
                return false;
            }

            if (tag is not null)
            {
                // A send may be queued before Connect supplies the target; its package then reports an empty one.
                tracker.Begin(tag, ((MsmtConnectOptions?)options)?.Target ?? unconnectedTarget, item.CancelSource);
            }

            // Counts a send as outstanding until it finishes processing (see SendOverConnection), so idle
            // eviction (IsIdle) never targets a client with a send still queued or in flight. Maintenance
            // is not traffic, so it never keeps a client from counting as idle.
            if (kind == PendingKind.Message)
            {
                Interlocked.Increment(ref outstandingSends);
            }

            queue.Enqueue(item, (-priority, sendSequence++));
        }

        // Raised before releasing the queue signal, so a background loop that wakes immediately can never
        // process (and report further status for) this item before its Queued status has been observed.
        RaisePackageChanged(tag, MsmtSendStatus.Queued);
        queueSignal.Release();
        return true;
    }

    private async Task ProcessQueueLoop()
    {
        while (true)
        {
            try
            {
                await queueSignal.WaitAsync(disposalCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                DrainQueueOnShutdown();
                return;
            }

            PendingSend? item;
            lock (queueLock)
            {
                queue.TryDequeue(out item, out _);
            }

            if (item is null)
            {
                continue;
            }

            if (item.Kind == PendingKind.Maintenance)
            {
                HandleMaintenance(item);
                continue;
            }

            if (item.CancelSource.IsCancellationRequested)
            {
                item.Payload.Dispose();
                RaisePackageChanged(item.Tag, MsmtSendStatus.Cancelled);
                item.ResponseSource?.TrySetCanceled();
                item.CancelSource.Dispose();
                Interlocked.Decrement(ref outstandingSends);
                continue;
            }

            if (item.Cancellation.IsCancellationRequested)
            {
                item.Payload.Dispose();
                RaisePackageChanged(item.Tag, MsmtSendStatus.Completed);
                item.ResponseSource?.TrySetCanceled(item.Cancellation);
                item.CancelSource.Dispose();
                Interlocked.Decrement(ref outstandingSends);
                continue;
            }

            try
            {
                await SendOverConnection(item);
            }
            catch (OperationCanceledException exception)
            {
                item.ResponseSource?.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                item.ResponseSource?.TrySetException(exception);
            }
        }
    }

    private void DrainQueueOnShutdown()
    {
        List<PendingSend> abandoned;
        lock (queueLock)
        {
            abandoned = [.. queue.UnorderedItems.Select(entry => entry.Element)];
            queue.Clear();
        }

        foreach (PendingSend item in abandoned)
        {
            item.Payload.Dispose();
            RaisePackageChanged(item.Tag, MsmtSendStatus.Cancelled);
            item.ResponseSource?.TrySetCanceled();
            item.CancelSource.Dispose();

            if (item.Kind == PendingKind.Message)
            {
                Interlocked.Decrement(ref outstandingSends);
            }
        }
    }

    /// <summary>
    /// Runs a maintenance check as a queued item, so it never races a concurrently processed send for the
    /// same connection - by the time this runs, nothing else is mid-cycle. Closes a <see
    /// cref="MsmtOperationMode.Session"/> connection whose negotiated lifetime has ended, or one the remote
    /// peer closed while it sat idle (raising <see cref="Unlinked"/> promptly instead of only when the next
    /// send fails), and otherwise queues a keep-alive once a Session connection has been silent for its
    /// randomized interval.
    /// </summary>
    /// <param name="item">The maintenance placeholder item to dispose of once handled.</param>
    private void HandleMaintenance(PendingSend item)
    {
        try
        {
            Interlocked.Exchange(ref maintenanceQueued, 0);

            if (connection is null)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;

            if (options.Mode == MsmtOperationMode.Session && now >= sessionExpiresAtUtc)
            {
                CloseConnection();
            }
            else if (!IsSocketAlive())
            {
                CloseConnection(new IOException("The remote peer closed the connection while it was idle."));
            }
            else if (options.Mode == MsmtOperationMode.Session && now - new DateTime(Interlocked.Read(ref lastWireActivityTicks), DateTimeKind.Utc) >= keepAliveInterval)
            {
                Enqueue(EmptyMemoryOwner.Instance, MsmtMessageFlags.ReachabilityCheck, null, 0, null, disposalCancellation.Token, null);
            }
        }
        finally
        {
            item.CancelSource.Dispose();
        }
    }

    private async Task MaintenanceLoop()
    {
        using PeriodicTimer timer = new(maintenanceInterval);
        while (await timer.WaitForNextTickAsync(disposalCancellation.Token))
        {
            if (connection is not null && Interlocked.CompareExchange(ref maintenanceQueued, 1, 0) == 0)
            {
                Enqueue(EmptyMemoryOwner.Instance, MsmtMessageFlags.None, null, 0, null, disposalCancellation.Token, null, PendingKind.Maintenance);
            }
        }
    }

    /// <summary>
    /// Determines whether the remote peer has left the connection open. This client never reads while
    /// idle, so a closed or reset connection would otherwise go unnoticed until the next send failed on it.
    /// A socket that is readable with nothing to read has been closed by the peer; unread bytes count as
    /// alive, since they may be a TLS message rather than the end of the stream.
    /// </summary>
    /// <returns><see langword="false"/> if the connection is known to be closed.</returns>
    private bool IsSocketAlive()
    {
        Socket? socket = tcpClient?.Client;
        if (socket is null)
        {
            return false;
        }

        try
        {
            return !socket.Poll(0, SelectMode.SelectError) && !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    private Exception TranslateFailure(Exception exception, CancellationToken cancellation, MsmtWatchdog? connectionWatchdog)
    {
        if (exception is OperationCanceledException or TimeoutException)
        {
            return exception;
        }

        // A forced socket close (via the cancellation registration or a watchdog) surfaces as a generic
        // IOException/ObjectDisposedException from the handshake or stream, since BouncyCastle doesn't honor
        // cancellation tokens once blocked - report what actually caused it instead of a confusing
        // "connection closed" error.
        if (cancellation.IsCancellationRequested)
        {
            return new OperationCanceledException("The send was cancelled.", exception, cancellation);
        }

        return connectionWatchdog is { HasExpired: true }
            ? new TimeoutException("The remote peer did not respond within the configured timeout.", exception)
            : exception;
    }

    private Exception CloseAfterFailure(Exception exception, CancellationToken cancellation, MsmtWatchdog? connectionWatchdog)
    {
        Exception failure = TranslateFailure(exception, cancellation, connectionWatchdog);
        CloseConnection(failure is TimeoutException ? failure : exception);
        return failure;
    }

    private async Task<(MsmtHeader Header, IMemoryOwner<byte> Payload)> Exchange(Stream stream, MsmtWatchdog connectionWatchdog, MsmtHeader requestHeader, ReadOnlyMemory<byte> requestPayload, string mismatchMessage, Action? onRequestWritten, CancellationToken cancellation)
    {
        byte[] headerBuffer = new byte[MsmtHeader.Size];
        requestHeader.Write(headerBuffer);

        using (connectionWatchdog.Guard(options.StallTimeout, true))
        {
            await stream.WriteAsync(headerBuffer, cancellation);
            if (requestPayload.Length > 0)
            {
                await stream.WriteAsync(requestPayload, cancellation);
            }
        }

        onRequestWritten?.Invoke();

        MsmtHeader responseHeader;
        using (connectionWatchdog.Guard(options.ResponseTimeout, false))
        {
            await MsmtProtocol.ReadExact(stream, headerBuffer, cancellation);
            responseHeader = MsmtHeader.Read(headerBuffer);
        }

        if (!responseHeader.IsWellFormed() || !responseHeader.Acknowledges(requestHeader))
        {
            throw new InvalidOperationException(mismatchMessage);
        }

        using (connectionWatchdog.Guard(options.StallTimeout, true))
        {
            return (responseHeader, await MsmtProtocol.ReadPooled(stream, (int)responseHeader.Length, cancellation));
        }
    }

    private async Task SendOverConnection(PendingSend item)
    {
        using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(item.Cancellation, item.CancelSource.Token, disposalCancellation.Token);
        CancellationToken cancellation = linkedCancellation.Token;

        // BouncyCastle's TLS stream falls back to a blocking byte[]-based ReadAsync/WriteAsync overload it
        // never overrides internally (see MsmtServer.DisposeAsync), so cancelling this token alone would
        // never actually interrupt a write or acknowledgement read already in progress. Forcing the raw
        // socket closed the instant any of the three linked tokens fire - this item's own cancellation
        // token, Cancel(tag) via CancelSource, or this client's own disposal - is the only way cancellation
        // here takes effect promptly instead of leaving the send blocked indefinitely.
        using CancellationTokenRegistration forceCloseOnCancellation = cancellation.Register(ForceCloseSocket);

        bool isKeepAlive = (item.Flags & MsmtMessageFlags.ReachabilityCheck) == MsmtMessageFlags.ReachabilityCheck;

        try
        {
            try
            {
                RekeyableTlsClientProtocol protocol = await EnsureConnection(cancellation);
                MsmtWatchdog connectionWatchdog = watchdog!;

                if (item.Dscp is { } dscp)
                {
                    ApplyDscp(dscp);
                }

                MsmtHeader requestHeader = new()
                {
                    Version = MsmtHeader.SupportedVersion,
                    Flags = item.Flags,
                    MessageId = MsmtProtocol.GenerateMessageId(),
                    Length = (uint)item.Payload.Memory.Length,
                };

                MsmtHeader responseHeader;
                IMemoryOwner<byte> responsePayload;

                try
                {
                    RaisePackageChanged(item.Tag, MsmtSendStatus.Transmitting);

                    void OnRequestWritten()
                    {
                        if ((item.Flags & MsmtMessageFlags.AcknowledgementRequestedOrGiven) == MsmtMessageFlags.AcknowledgementRequestedOrGiven)
                        {
                            RaisePackageChanged(item.Tag, MsmtSendStatus.PendingAcknowledgement);
                        }
                    }

                    (responseHeader, responsePayload) = await Exchange(protocol.Stream, connectionWatchdog, requestHeader, item.Payload.Memory, "The server's acknowledgement did not correspond to the sent message.", OnRequestWritten, cancellation);
                }
                catch (Exception exception)
                {
                    Exception failure = CloseAfterFailure(exception, cancellation, connectionWatchdog);
                    if (ReferenceEquals(failure, exception))
                    {
                        throw;
                    }

                    throw failure;
                }

                bool success = (responseHeader.Flags & MsmtMessageFlags.MessageSuccess) == MsmtMessageFlags.MessageSuccess;

                if (item.ResponseSource is { } responseSource)
                {
                    responseSource.SetResult(new MsmtResponse { Success = success, Payload = responsePayload });
                }
                else
                {
                    responsePayload.Dispose();
                }

                messagesSinceConnect++;
                long completedTicks = DateTime.UtcNow.Ticks;
                Interlocked.Exchange(ref lastWireActivityTicks, completedTicks);

                if (!isKeepAlive)
                {
                    Interlocked.Exchange(ref lastActivityTicks, completedTicks);
                }

                if (options.Mode == MsmtOperationMode.Message)
                {
                    CloseConnection();
                }
                else if (options.Mode == MsmtOperationMode.MessageWithRekeying)
                {
                    if (messagesSinceConnect >= options.RekeyLimit)
                    {
                        CloseConnection();
                    }
                    else
                    {
                        try
                        {
                            protocol.Rekey();
                        }
                        catch (Exception exception)
                        {
                            CloseConnection(exception);
                            throw;
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException && cancellation.IsCancellationRequested)
            {
                throw new OperationCanceledException("The send was cancelled.", exception, cancellation);
            }
            finally
            {
                item.Payload.Dispose();
            }
        }
        finally
        {
            RaisePackageChanged(item.Tag, item.CancelSource.IsCancellationRequested ? MsmtSendStatus.Cancelled : MsmtSendStatus.Completed);
            item.CancelSource.Dispose();
            Interlocked.Decrement(ref outstandingSends);
        }
    }

    private async Task<RekeyableTlsClientProtocol> EnsureConnection(CancellationToken cancellation)
    {
        if (connection is not null)
        {
            bool isReusable = options.Mode == MsmtOperationMode.MessageWithRekeying
                || (options.Mode == MsmtOperationMode.Session && DateTime.UtcNow < sessionExpiresAtUtc);

            if (isReusable)
            {
                if (IsSocketAlive())
                {
                    return connection;
                }

                // Nothing has been written to it yet, so the send can safely proceed over a fresh connection.
                CloseConnection(new IOException("The remote peer closed the connection while it was idle."));
            }
        }

        CloseConnection();
        RekeyableTlsClientProtocol protocol;

        try
        {
            Linking.Invoke(this, new MsmtLinkingEventArgs { Link = this });
            protocol = await OpenConnection(cancellation);
        }
        catch (Exception exception)
        {
            CloseConnection();
            LinkFailed.Invoke(this, new MsmtLinkFailedEventArgs { Link = this, Exception = exception });
            throw;
        }

        connection = protocol;
        Linked.Invoke(this, new MsmtLinkedEventArgs { Link = this });

        if (options.Mode == MsmtOperationMode.Session)
        {
            MsmtWatchdog connectionWatchdog = watchdog!;

            try
            {
                sessionExpiresAtUtc = await NegotiateSession(protocol, connectionWatchdog, cancellation);
            }
            catch (Exception exception)
            {
                Exception failure = CloseAfterFailure(exception, cancellation, connectionWatchdog);
                if (ReferenceEquals(failure, exception))
                {
                    throw;
                }

                throw failure;
            }

            keepAliveInterval = options.KeepAliveMinInterval + ((options.KeepAliveMaxInterval - options.KeepAliveMinInterval) * keepAliveRandom.NextDouble());
            Interlocked.Exchange(ref lastWireActivityTicks, DateTime.UtcNow.Ticks);
        }

        return protocol;
    }

    private async Task<RekeyableTlsClientProtocol> OpenConnection(CancellationToken cancellation)
    {
        TcpClient client = new();
        MsmtWatchdog connectionWatchdog = new(() => CloseSocket(client));
        tcpClient = client;
        watchdog = connectionWatchdog;

        try
        {
            using (connectionWatchdog.Guard(options.HandshakeTimeout, false))
            {
                await client.ConnectAsync(options.Target.Host, options.Target.Port, cancellation);
                MsmtProtocol.ApplyTcpKeepAlive(client.Client, options.TcpKeepAliveTime);

                RekeyableTlsClientProtocol protocol = new(new MsmtWatchedStream(client.GetStream(), connectionWatchdog));
                MsmtTlsClient tlsClient = new(options);
                protocol.Connect(tlsClient);
                remoteIdentity = tlsClient.ServerIdentity;
                messagesSinceConnect = 0;
                return protocol;
            }
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or TimeoutException) && connectionWatchdog.HasExpired)
        {
            throw new TimeoutException("The connection did not complete its handshake within the configured timeout.", exception);
        }
    }

    // Applied per send rather than once per connection, since a cached connection (Session Mode or
    // Message Mode with Rekeying) may be reused across sends requesting different values. Applied
    // unconditionally rather than gated on AddressFamily: TcpClient's cancellable ConnectAsync overload
    // creates a dual-stack socket reporting InterNetworkV6 even for a plain IPv4 target, and IP_TOS still
    // takes effect on it. Marking is inherently best-effort per the ICD, so a platform that rejects it
    // doesn't fail the send.
    private void ApplyDscp(int dscp)
    {
        try
        {
            tcpClient!.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, dscp << 2);
        }
        catch (SocketException)
        {
        }
    }

    private async Task<DateTime> NegotiateSession(RekeyableTlsClientProtocol protocol, MsmtWatchdog connectionWatchdog, CancellationToken cancellation)
    {
        byte[] payload = Encoding.ASCII.GetBytes(((int)options.SessionLifetime.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        const string mismatchMessage = "The server's session negotiation reply did not correspond to the sent request.";

        MsmtHeader requestHeader = new()
        {
            Version = MsmtHeader.SupportedVersion,
            Flags = MsmtMessageFlags.SessionModeNegotiation | MsmtMessageFlags.MessageSuccess,
            MessageId = MsmtProtocol.GenerateMessageId(),
            Length = (uint)payload.Length,
        };

        (MsmtHeader responseHeader, IMemoryOwner<byte> responsePayload) = await Exchange(protocol.Stream, connectionWatchdog, requestHeader, payload, mismatchMessage, null, cancellation);

        using (responsePayload)
        {
            if ((responseHeader.Flags & MsmtMessageFlags.SessionModeNegotiation) == 0)
            {
                throw new InvalidOperationException(mismatchMessage);
            }

            if (responseHeader.Flags != MsmtMessageFlags.SessionModeAccepted)
            {
                throw new InvalidOperationException("The remote MSMT server rejected or does not support Session Mode.");
            }

            int agreedSeconds = int.Parse(Encoding.ASCII.GetString(responsePayload.Memory.Span), CultureInfo.InvariantCulture);
            return DateTime.UtcNow.AddSeconds(agreedSeconds);
        }
    }

    private void RaisePackageChanged(object? tag, MsmtSendStatus status)
    {
        if (tag is not null && tracker.SetStatus(tag, status) is { } package)
        {
            PackageChanged.Invoke(this, new MsmtPackageChangedEventArgs { Link = this, Package = package, Status = status });
        }
    }

    private void CloseConnection(Exception? exception = null)
    {
        // Only a connection that was fully established and secured (Linked already raised for it) ever
        // raises Unlinked; a failed attempt that never got that far raises LinkFailed instead, at its own
        // call site, since there is no established connection here to surface.
        // Exchanged atomically since Dispose may run this concurrently with the processing loop, and each
        // established connection must raise Unlinked exactly once.
        if (Interlocked.Exchange(ref connection, null) is { } closing)
        {
            try
            {
                closing.Close();
            }
            catch (IOException)
            {
            }

            Unlinked.Invoke(this, new MsmtUnlinkedEventArgs { Link = this, Exception = exception });
        }

        Interlocked.Exchange(ref tcpClient, null)?.Dispose();
        Interlocked.Exchange(ref watchdog, null)?.Dispose();
    }

    private enum PendingKind
    {
        Message,
        Maintenance,
    }

    private sealed record PendingSend
    {
        public required IMemoryOwner<byte> Payload { get; init; }

        public required MsmtMessageFlags Flags { get; init; }

        public object? Tag { get; init; }

        public int? Dscp { get; init; }

        public required CancellationToken Cancellation { get; init; }

        public TaskCompletionSource<MsmtResponse>? ResponseSource { get; init; }

        public PendingKind Kind { get; init; }

        public CancellationTokenSource CancelSource { get; } = new();
    }
}
