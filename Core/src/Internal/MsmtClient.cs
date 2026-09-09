namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// Sends MSMT messages to a single remote server, queuing send requests so calling code never blocks on
/// the underlying TLS connection. Used internally by <see cref="MsmtPeer"/> to back each outgoing
/// connection it creates on demand.
/// </summary>
/// <remarks>
/// Queued sends are processed one at a time, in priority order and first-in-first-out within a priority,
/// by a single background loop that also owns the underlying connection. A second background loop
/// automatically sends a reachability check as a keep-alive whenever an established <see
/// cref="MsmtOperationMode.Session"/> connection has been idle for a randomized three-to-five-minute
/// interval, per the ICD. The TLS handshake runs on the BouncyCastle TLS engine's own blocking API (there
/// is no cancellable async overload), so a cancellation token passed to a <c>Send</c> overload, or <see
/// cref="Cancel"/> called against a tagged send's own tag, cannot interrupt a handshake already in
/// progress; once connected, either one instead forces the underlying socket closed the instant it fires
/// (see <see cref="SendOverConnection"/>), promptly unblocking a write or acknowledgement read already in
/// flight - the BouncyCastle stream itself does not honor the token on a read/write already blocked, only
/// a direct socket close does. Since sending only enqueues a payload and never
/// surfaces an exception directly, <see cref="Unlinked"/> also fires - carrying the causing exception -
/// when a connection attempt itself fails, not only when an already-established connection closes.
/// </remarks>
internal sealed class MsmtClient : IMsmtLink
{
    private readonly PriorityQueue<PendingSend, (int NegatedPriority, long Sequence)> queue = new();
    private readonly Lock queueLock = new();
    private readonly SemaphoreSlim queueSignal = new(0);
    private readonly CancellationTokenSource disposalCancellation = new();
    private readonly Random keepAliveRandom = new();
    private readonly TimeSpan keepAliveCheckInterval = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<object, PendingSend> activeSends = new();
    private readonly ConcurrentDictionary<object, MsmtSendStatus> sendStatuses = new();
    private readonly ConcurrentQueue<object> completedSendTags = new();
    private readonly int maxTrackedCompletedSendStatuses = 10_000;

    private MsmtConnectOptions options = null!;
    private Action? removeFromCache;
    private long sendSequence;
    private Task? processingLoop;
    private Task? keepAliveLoop;
    private TcpClient? tcpClient;
    private RekeyableTlsClientProtocol? connection;
    private MsmtIdentity? remoteIdentity;
    private DateTime sessionExpiresAtUtc;
    private long lastActivityTicks;
    private int outstandingSends;
    private int idleCheckScheduled;
    private TimeSpan keepAliveInterval;
    private int messagesSinceConnect;

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
    /// Gets a value indicating whether this client currently has no send queued or in flight. Used by <see
    /// cref="MsmtOptions.MaxIdleTime"/>/<see cref="MsmtOptions.MaxConnectionCount"/> eviction, and this
    /// client's own idle-timeout check, to never interrupt a send/request-response cycle in progress.
    /// </summary>
    internal bool IsIdle => Volatile.Read(ref outstandingSends) == 0;

    /// <summary>Gets the last time a send completed on this client's connection and it started waiting for the next one.</summary>
    internal DateTime LastActivityUtc => new(Interlocked.Read(ref lastActivityTicks), DateTimeKind.Utc);

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

    /// <summary>Gets a snapshot of this client's currently active (not yet completed or cancelled) tagged packages.</summary>
    public IReadOnlyList<IMsmtPackage> Packages => [.. activeSends.Keys.Select(GetPackage).OfType<IMsmtPackage>()];

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
        keepAliveLoop ??= Task.Run(KeepAliveLoop);
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
    public void Send(IMemoryOwner<byte> payload, MsmtSendOptions? options = null, CancellationToken cancellation = default)
    {
        MsmtProtocol.ValidatePayloadLength(payload.Memory.Length, nameof(payload));
        MsmtSendOptions sendOptions = options ?? new MsmtSendOptions();
        Enqueue(payload, MsmtMessageFlags.None, sendOptions.Tag, sendOptions.Priority, sendOptions.Dscp, cancellation, null);
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
    public Task<MsmtResponse> Request(IMemoryOwner<byte> payload, MsmtSendOptions? options = null, CancellationToken cancellation = default)
    {
        MsmtProtocol.ValidatePayloadLength(payload.Memory.Length, nameof(payload));
        MsmtSendOptions sendOptions = options ?? new MsmtSendOptions();
        TaskCompletionSource<MsmtResponse> responseSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(payload, MsmtMessageFlags.AcknowledgementRequestedOrGiven, sendOptions.Tag, sendOptions.Priority, sendOptions.Dscp, cancellation, responseSource);
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
    public Task<MsmtResponse> Request(ReadOnlyMemory<byte> payload, MsmtSendOptions? options = null, CancellationToken cancellation = default) =>
        Request(new NonOwningMemoryOwner(payload), options, cancellation);

    /// <summary>
    /// Requests cancellation of the still-outstanding tagged send previously queued via <see
    /// cref="Send(IMemoryOwner{byte}, MsmtSendOptions?, CancellationToken)"/> with <paramref name="tag"/>.
    /// Does nothing if no send is currently outstanding for that tag.
    /// </summary>
    /// <param name="tag">The tag identifying the send to cancel, as passed to <see cref="Send(IMemoryOwner{byte}, MsmtSendOptions?, CancellationToken)"/>.</param>
    public void Cancel(object tag)
    {
        if (activeSends.TryGetValue(tag, out PendingSend? item))
        {
            try
            {
                item.CancelSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The send raced to completion and disposed its CancelSource between TryGetValue above and
                // this call; nothing left to cancel.
            }
        }
    }

    /// <summary>
    /// Gets the current status of the tagged send previously queued via <see cref="Send(IMemoryOwner{byte},
    /// MsmtSendOptions?, CancellationToken)"/> with <paramref name="tag"/>. A completed or cancelled tag's
    /// status is retained only for a bounded number of the most recently finished sends, so this may also
    /// return <see langword="null"/> for a tag whose send finished long enough ago to have been forgotten.
    /// </summary>
    /// <param name="tag">The tag identifying the send to look up, as passed to <see cref="Send(IMemoryOwner{byte}, MsmtSendOptions?, CancellationToken)"/>.</param>
    /// <returns>The send's current status, or <see langword="null"/> if no send was ever queued with this tag, or its status has since been forgotten.</returns>
    public MsmtSendStatus? GetStatus(object tag) =>
        sendStatuses.TryGetValue(tag, out MsmtSendStatus status) ? status : null;

    /// <summary>
    /// Gets the package for the tagged send previously queued via <see cref="Send(IMemoryOwner{byte},
    /// MsmtSendOptions?, CancellationToken)"/> with <paramref name="tag"/>, or <see langword="null"/> if no
    /// send was ever queued with this tag, or its status has since been forgotten (see <see
    /// cref="GetStatus"/>).
    /// </summary>
    /// <param name="tag">The tag identifying the send to look up, as passed to <see cref="Send(IMemoryOwner{byte}, MsmtSendOptions?, CancellationToken)"/>.</param>
    public IMsmtPackage? GetPackage(object tag) =>
        sendStatuses.TryGetValue(tag, out MsmtSendStatus status) ? new MsmtPackage(this, tag, status) : null;

    /// <summary>
    /// Immediately abandons this client: cancels its background loops and closes its connection without
    /// waiting for either to finish. Prefer <see cref="DisposeAsync"/> for a clean, awaited shutdown; use
    /// this only when an immediate, non-blocking teardown is required.
    /// </summary>
    public void Dispose()
    {
        disposalCancellation.Cancel();
        DrainQueueOnShutdown();
        CloseConnection();
        disposalCancellation.Dispose();
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

        if (keepAliveLoop is not null)
        {
            try
            {
                await keepAliveLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        DrainQueueOnShutdown();
        CloseConnection();
        disposalCancellation.Dispose();
        queueSignal.Dispose();
    }

    /// <summary>Immediately closes the underlying socket, if any, without touching <see cref="MsmtClient"/>'s own connection bookkeeping.</summary>
    private void ForceCloseSocket()
    {
        try
        {
            tcpClient?.Close();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }

    private void Enqueue(IMemoryOwner<byte> payload, MsmtMessageFlags flags, object? tag, int priority, int? dscp, CancellationToken cancellation, TaskCompletionSource<MsmtResponse>? responseSource, bool isIdleCheck = false)
    {
        PendingSend item = new()
        {
            Payload = payload,
            Flags = flags,
            Tag = tag,
            Dscp = dscp,
            Cancellation = cancellation,
            ResponseSource = responseSource,
            IsIdleCheck = isIdleCheck,
        };

        if (tag is not null)
        {
            activeSends[tag] = item;
        }

        // Counts this item as outstanding until it finishes processing (see HandleIdleCheck/SendOverConnection),
        // so MaxIdleTime/MaxConnectionCount eviction (IsIdle) never targets a connection with a send still
        // queued or in flight.
        Interlocked.Increment(ref outstandingSends);

        lock (queueLock)
        {
            queue.Enqueue(item, (-priority, sendSequence++));
        }

        // Raised before releasing the queue signal, so a background loop that wakes immediately can never
        // process (and report further status for) this item before its Queued status has been observed.
        RaisePackageChanged(tag, MsmtSendStatus.Queued);
        queueSignal.Release();
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

            if (item.IsIdleCheck)
            {
                HandleIdleCheck(item);
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
            Interlocked.Decrement(ref outstandingSends);
        }
    }

    /// <summary>
    /// Closes this client's connection if it has gone <see cref="MsmtConnectOptions.MaxIdleTime"/> without a
    /// send, run as a queued item so it never races a concurrently processed real send for the same
    /// connection - by the time this runs, nothing else is mid-cycle. If the connection is still open but
    /// not yet actually idle that long - including because <see cref="ScheduleIdleCheck"/> clamped the wait
    /// short of the full configured duration - reschedules another check for whatever time remains.
    /// </summary>
    /// <param name="item">The idle-check placeholder item to dispose of once handled.</param>
    private void HandleIdleCheck(PendingSend item)
    {
        try
        {
            // Consumed here regardless of outcome: ScheduleIdleCheck below re-arms it if rescheduling, and
            // there's nothing left to debounce against once this connection is closing or already closed.
            Interlocked.Exchange(ref idleCheckScheduled, 0);

            if (connection is null || options.MaxIdleTime is not { } maxIdleTime)
            {
                return;
            }

            TimeSpan remaining = maxIdleTime - (DateTime.UtcNow - LastActivityUtc);
            if (remaining <= TimeSpan.Zero)
            {
                CloseConnection();
            }
            else
            {
                ScheduleIdleCheck(remaining);
            }
        }
        finally
        {
            item.Payload.Dispose();
            item.CancelSource.Dispose();
            Interlocked.Decrement(ref outstandingSends);
        }
    }

    private async Task KeepAliveLoop()
    {
        using PeriodicTimer timer = new(keepAliveCheckInterval);
        while (await timer.WaitForNextTickAsync(disposalCancellation.Token))
        {
            bool sessionIdle =
                options.Mode == MsmtOperationMode.Session
                && connection is not null
                && DateTime.UtcNow < sessionExpiresAtUtc
                && DateTime.UtcNow - LastActivityUtc >= keepAliveInterval;

            if (sessionIdle)
            {
                Enqueue(EmptyMemoryOwner.Instance, MsmtMessageFlags.ReachabilityCheck, null, 0, null, disposalCancellation.Token, null);
            }
        }
    }

    /// <summary>
    /// Schedules a one-shot check, at most <paramref name="delay"/> from now (see <see
    /// cref="MsmtProtocol.ClampToMaxTimerDuration"/>), of whether this client's connection has since gone
    /// idle - queued rather than run directly on this timer, so it can never race a concurrently processed
    /// send for the same connection (see <see cref="HandleIdleCheck"/>). A no-op if a check is already
    /// scheduled, since that one will reschedule itself for the correct remaining time if it turns out to
    /// still be too early once it runs.
    /// </summary>
    /// <param name="delay">How long from now to check, at most.</param>
    private void ScheduleIdleCheck(TimeSpan delay)
    {
        if (Interlocked.CompareExchange(ref idleCheckScheduled, 1, 0) != 0)
        {
            return;
        }

        TimeSpan clampedDelay = MsmtProtocol.ClampToMaxTimerDuration(delay);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(clampedDelay, disposalCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                Enqueue(EmptyMemoryOwner.Instance, MsmtMessageFlags.None, null, 0, null, disposalCancellation.Token, null, isIdleCheck: true);
            }
            catch (ObjectDisposedException)
            {
                // Raced this client's own disposal completing (queueSignal already disposed); nothing left to check.
            }
        });
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

        try
        {
            try
            {
                RekeyableTlsClientProtocol protocol = await EnsureConnection(cancellation);
                Stream stream = protocol.Stream;

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

                    byte[] headerBuffer = new byte[MsmtHeader.Size];
                    requestHeader.Write(headerBuffer);
                    await stream.WriteAsync(headerBuffer, cancellation);
                    if (item.Payload.Memory.Length > 0)
                    {
                        await stream.WriteAsync(item.Payload.Memory, cancellation);
                    }

                    if ((item.Flags & MsmtMessageFlags.AcknowledgementRequestedOrGiven) == MsmtMessageFlags.AcknowledgementRequestedOrGiven)
                    {
                        RaisePackageChanged(item.Tag, MsmtSendStatus.PendingAcknowledgement);
                    }

                    await MsmtProtocol.ReadExact(stream, headerBuffer, cancellation);
                    responseHeader = MsmtHeader.Read(headerBuffer);

                    if (!responseHeader.IsWellFormed() || !responseHeader.Acknowledges(requestHeader))
                    {
                        throw new InvalidOperationException("The server's acknowledgement did not correspond to the sent message.");
                    }

                    responsePayload = await MsmtProtocol.ReadPooled(stream, (int)responseHeader.Length, cancellation);
                }
                catch (Exception exception)
                {
                    CloseConnection(exception);

                    // A forced socket close (via the cancellation registration above) surfaces as a generic
                    // IOException/ObjectDisposedException from the stream, not OperationCanceledException,
                    // since BouncyCastle's TLS stream doesn't honor cancellation tokens on a blocked
                    // read/write - report it as a cancellation instead of a confusing "connection closed"
                    // error whenever cancellation is what actually triggered it.
                    if (cancellation.IsCancellationRequested)
                    {
                        throw new OperationCanceledException("The send was cancelled.", exception, cancellation);
                    }

                    throw;
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
                Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);

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

                // No-op for Message Mode, which already closed the connection above - there is nothing left
                // to time out until the next send opens a fresh one.
                if (connection is not null && options.MaxIdleTime is { } maxIdleTime)
                {
                    ScheduleIdleCheck(maxIdleTime);
                }
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
            if (options.Mode == MsmtOperationMode.MessageWithRekeying)
            {
                return connection;
            }

            if (options.Mode == MsmtOperationMode.Session && DateTime.UtcNow < sessionExpiresAtUtc)
            {
                return connection;
            }
        }

        CloseConnection();
        Linking.Invoke(this, new MsmtLinkingEventArgs { Link = this });
        RekeyableTlsClientProtocol protocol;

        try
        {
            protocol = await OpenConnection(cancellation);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CloseConnection();
            LinkFailed.Invoke(this, new MsmtLinkFailedEventArgs { Link = this, Exception = exception });
            throw;
        }

        connection = protocol;
        Linked.Invoke(this, new MsmtLinkedEventArgs { Link = this });

        if (options.Mode == MsmtOperationMode.Session)
        {
            try
            {
                sessionExpiresAtUtc = await NegotiateSession(protocol, cancellation);
            }
            catch (Exception exception)
            {
                CloseConnection(exception);
                throw;
            }

            keepAliveInterval = TimeSpan.FromSeconds(keepAliveRandom.Next(180, 301));
            Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);
        }

        return protocol;
    }

    private async Task<RekeyableTlsClientProtocol> OpenConnection(CancellationToken cancellation)
    {
        TcpClient client = new();
        tcpClient = client;
        await client.ConnectAsync(options.Target.Host, options.Target.Port, cancellation);

        RekeyableTlsClientProtocol protocol = new(client.GetStream());
        MsmtTlsClient tlsClient = new(options);
        protocol.Connect(tlsClient);
        remoteIdentity = tlsClient.ServerIdentity;
        messagesSinceConnect = 0;
        return protocol;
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

    private async Task<DateTime> NegotiateSession(RekeyableTlsClientProtocol protocol, CancellationToken cancellation)
    {
        Stream stream = protocol.Stream;
        byte[] payload = Encoding.ASCII.GetBytes(((int)options.SessionLifetime.TotalSeconds).ToString(CultureInfo.InvariantCulture));

        MsmtHeader requestHeader = new()
        {
            Version = MsmtHeader.SupportedVersion,
            Flags = MsmtMessageFlags.SessionModeNegotiation | MsmtMessageFlags.MessageSuccess,
            MessageId = MsmtProtocol.GenerateMessageId(),
            Length = (uint)payload.Length,
        };

        byte[] headerBuffer = new byte[MsmtHeader.Size];
        requestHeader.Write(headerBuffer);
        await stream.WriteAsync(headerBuffer, cancellation);
        await stream.WriteAsync(payload, cancellation);

        await MsmtProtocol.ReadExact(stream, headerBuffer, cancellation);
        MsmtHeader responseHeader = MsmtHeader.Read(headerBuffer);

        if (!responseHeader.IsWellFormed() || !responseHeader.Acknowledges(requestHeader) || (responseHeader.Flags & MsmtMessageFlags.SessionModeNegotiation) == 0)
        {
            throw new InvalidOperationException("The server's session negotiation reply did not correspond to the sent request.");
        }

        byte[] responsePayload = [];
        if (responseHeader.Length > 0)
        {
            responsePayload = new byte[responseHeader.Length];
            await MsmtProtocol.ReadExact(stream, responsePayload, cancellation);
        }

        if (responseHeader.Flags != MsmtMessageFlags.SessionModeAccepted)
        {
            throw new InvalidOperationException("The remote MSMT server rejected or does not support Session Mode.");
        }

        int agreedSeconds = int.Parse(Encoding.ASCII.GetString(responsePayload), CultureInfo.InvariantCulture);
        return DateTime.UtcNow.AddSeconds(agreedSeconds);
    }

    private void RaisePackageChanged(object? tag, MsmtSendStatus status)
    {
        if (tag is null)
        {
            return;
        }

        sendStatuses[tag] = status;

        if (status is MsmtSendStatus.Completed or MsmtSendStatus.Cancelled)
        {
            activeSends.TryRemove(tag, out _);

            // Bounds otherwise-unbounded growth of sendStatuses for a long-lived client given a uniquely
            // tagged send per message: only a completed/cancelled tag - never one still active - is queued
            // for eventual eviction, so GetStatus keeps working for every in-flight or recently finished
            // send and only forgets a tag once far enough behind more recent completions.
            completedSendTags.Enqueue(tag);
            while (completedSendTags.Count > maxTrackedCompletedSendStatuses && completedSendTags.TryDequeue(out object? oldestTag))
            {
                sendStatuses.TryRemove(oldestTag, out _);
            }
        }

        PackageChanged.Invoke(this, new MsmtPackageChangedEventArgs { Link = this, Package = new MsmtPackage(this, tag, status), Status = status });
    }

    private void CloseConnection(Exception? exception = null)
    {
        // Only a connection that was fully established and secured (Linked already raised for it) ever
        // raises Unlinked; a failed attempt that never got that far raises LinkFailed instead, at its own
        // call site, since there is no established connection here to surface.
        if (connection is not null)
        {
            try
            {
                connection.Close();
            }
            catch (IOException)
            {
            }

            connection = null;
            Unlinked.Invoke(this, new MsmtUnlinkedEventArgs { Link = this, Exception = exception });
        }

        tcpClient?.Dispose();
        tcpClient = null;
    }

    private sealed record PendingSend
    {
        public required IMemoryOwner<byte> Payload { get; init; }

        public required MsmtMessageFlags Flags { get; init; }

        public object? Tag { get; init; }

        public int? Dscp { get; init; }

        public required CancellationToken Cancellation { get; init; }

        public TaskCompletionSource<MsmtResponse>? ResponseSource { get; init; }

        public bool IsIdleCheck { get; init; }

        public CancellationTokenSource CancelSource { get; } = new();
    }
}
