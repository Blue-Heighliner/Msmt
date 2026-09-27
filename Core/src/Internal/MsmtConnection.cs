namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// One MSMT connection, in either role and in any mode: the TLS channel plus everything MSMT layers on it.
/// A reader loop parses messages and routes each one, either to the request this side is waiting on as its
/// acknowledgement or, when the remote side sent it, to the application; a handler loop delivers those
/// received messages and writes their acknowledgements. Both sides may have a message in flight at once,
/// one per direction, which is what makes a session connection bidirectional. Carries no events of its
/// own - everything it would otherwise raise is reported through the callbacks its owner supplies at
/// construction, so <see cref="MsmtSessionPeer"/> can republish them peer-wide. The public <see
/// cref="IMsmtSessionPeer"/> and <see cref="IMsmtPeer"/> are built from these.
/// </summary>
/// <remarks>
/// The ICD gives a message and its acknowledgement the same header, so a message is told apart from an
/// acknowledgement by its message ID: an ID that matches the request this side has in flight is its
/// acknowledgement, and anything else is a new request. Message IDs are random, but the low bit is fixed by
/// role (the initiator's are even, the acceptor's odd) so the two sides can never pick the same one.
/// </remarks>
internal sealed class MsmtConnection : IMsmtConnection, IMsmtEvictable
{
    /// <summary>Creates a connection, immediately <see cref="MsmtConnectionStatus.Connecting"/>. Call <see cref="AttachChannel"/> before starting to connect it, then <see cref="CompleteHandshake"/> or <see cref="FailToConnect"/> once the handshake resolves.</summary>
    /// <param name="settings">How this connection behaves.</param>
    /// <param name="remote">The remote peer's address and port.</param>
    /// <param name="direction">Which side opened this connection.</param>
    /// <param name="tracker">Tracks this connection's tagged sends, or <see langword="null"/> for a tracker of its own.</param>
    /// <param name="onReceived">Invoked and awaited with a message the remote peer sent, or <see langword="null"/> to accept it automatically without reporting it anywhere.</param>
    /// <param name="onPackageChanged">Invoked as a tagged send's status changes.</param>
    /// <param name="onDisconnected">Invoked once, when this connection ends, however it ends - including never having connected at all.</param>
    public MsmtConnection(
        MsmtConnectionSettings settings,
        MsmtTarget remote,
        MsmtConnectionDirection direction,
        IMsmtPackageTracker? tracker = null,
        Func<MsmtConnection, ReadOnlyMemory<byte>, bool, ValueTask<MsmtReceiveResult?>>? onReceived = null,
        Action<MsmtConnection, MsmtPackageChange>? onPackageChanged = null,
        Action<MsmtConnection, Exception?>? onDisconnected = null)
    {
        this.settings = settings;
        this.remote = remote;
        Direction = direction;
        this.tracker = tracker ?? new MsmtPackageTracker();
        this.onReceived = onReceived;
        this.onDisconnected = onDisconnected;
        isSession = settings.Role == MsmtConnectionRole.Initiator && settings.Mode == MsmtConnectionMode.Session;
        outbox = new MsmtOutbox(this.tracker, () => remote, Exchange, args => onPackageChanged?.Invoke(this, args));
    }

    private readonly MsmtConnectionSettings settings;
    private readonly MsmtTarget remote;
    private readonly IMsmtPackageTracker tracker;
    private readonly IMsmtOutbox outbox;
    private readonly Func<MsmtConnection, ReadOnlyMemory<byte>, bool, ValueTask<MsmtReceiveResult?>>? onReceived;
    private readonly Action<MsmtConnection, Exception?>? onDisconnected;
    private readonly Channel<MsmtFrame> inbound = Channel.CreateBounded<MsmtFrame>(new BoundedChannelOptions(4) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly TaskCompletionSource<bool> connectingResolved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource closedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Random keepAliveRandom = new();
    private readonly TimeSpan maintenanceInterval = TimeSpan.FromSeconds(1);
    private readonly TimeSpan sessionExpiryGrace = TimeSpan.FromSeconds(2);

    // Cancelled by Dispose/DisposeAsync while still Connecting, to abort whatever the connector is doing;
    // never disposed, since the connector's background task may still read its Token after that.
    private readonly CancellationTokenSource connectingCancellation = new();

    // Never disposed: the loops may still read its Token after the connection closes, which throws once
    // disposed, and it holds nothing that needs releasing.
    private readonly CancellationTokenSource lifetime = new();

    private IMsmtTlsChannel? channel;
    private PendingRequest? currentRequest;
    private MsmtFrame? pendingFirstFrame;
    private Exception? closeReason;
    private bool isSession;
    private MsmtIdentity? identity;
    private DateTime? expiration;
    private TimeSpan keepAliveInterval;
    private long lastWireActivityTicks = DateTime.UtcNow.Ticks;
    private long lastInboundActivityTicks = DateTime.UtcNow.Ticks;
    private int messageCount;
    private int inboundBusy;
    private int keepAliveQueued;
    private int status;

    /// <inheritdoc />
    public MsmtConnectionStatus Status => (MsmtConnectionStatus)Volatile.Read(ref status);

    /// <inheritdoc />
    public MsmtConnectionDirection Direction { get; }

    /// <inheritdoc />
    public MsmtTarget Remote => remote;

    /// <inheritdoc />
    public MsmtIdentity? Identity => identity;

    /// <inheritdoc />
    public DateTime? Expiration => expiration;

    /// <summary>Gets a task that completes once this connection has closed and its <c>onDisconnected</c> callback has run.</summary>
    public Task Closed => closedSource.Task;

    /// <summary>Gets a value indicating whether nothing is queued, in flight, or being handled in either direction, so this connection may be evicted without interrupting a message.</summary>
    public bool IsIdle => outbox.IsIdle && Volatile.Read(ref inboundBusy) == 0 && Volatile.Read(ref currentRequest) is null;

    /// <summary>Gets the last time an application message finished in either direction, not counting keep-alives, reachability checks, or session negotiation.</summary>
    public DateTime LastActivityUtc
    {
        get
        {
            DateTime sent = outbox.LastActivityUtc;
            DateTime handled = new(Interlocked.Read(ref lastInboundActivityTicks), DateTimeKind.Utc);
            return sent > handled ? sent : handled;
        }
    }

    /// <summary>Gets the last time any message finished in either direction, including keep-alives and reachability checks. Exposed for diagnostics and testing.</summary>
    public DateTime LastWireActivityUtc => new(Interlocked.Read(ref lastWireActivityTicks), DateTimeKind.Utc);

    /// <summary>Gets the reason this connection failed to connect, or later closed, or <see langword="null"/> if it closed normally.</summary>
    public Exception? CloseReason => closeReason;

    /// <summary>Gets the token the connector should link into its own connect/handshake work, cancelled if this connection is disposed while still <see cref="MsmtConnectionStatus.Connecting"/>.</summary>
    internal CancellationToken ConnectingToken => connectingCancellation.Token;

    /// <inheritdoc />
    public async Task<bool> Wait(CancellationToken cancellation = default) => await connectingResolved.Task.WaitAsync(cancellation);

    /// <inheritdoc />
    public void Send(IMemoryOwner<byte> payload, MsmtSendOptions? options = null)
    {
        MsmtProtocol.ValidatePayloadLength(payload.Memory.Length, nameof(payload));
        MsmtSendOptions sendOptions = options ?? new MsmtSendOptions();
        bool isQueued = outbox.Enqueue(new MsmtPendingSend { Payload = payload, Flags = MsmtMessageFlags.None, Tag = sendOptions.Tag, Dscp = sendOptions.Dscp, Cancellation = CancellationToken.None }, sendOptions.Priority);
        ObjectDisposedException.ThrowIf(!isQueued, this);
    }

    /// <inheritdoc />
    public Task<MsmtResponse> Request(IMemoryOwner<byte> payload, MsmtSendOptions? options = null, CancellationToken cancellation = default)
    {
        MsmtProtocol.ValidatePayloadLength(payload.Memory.Length, nameof(payload));
        MsmtSendOptions sendOptions = options ?? new MsmtSendOptions();
        TaskCompletionSource<MsmtResponse> responseSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool isQueued = outbox.Enqueue(new MsmtPendingSend { Payload = payload, Flags = MsmtMessageFlags.AcknowledgementRequestedOrGiven, Tag = sendOptions.Tag, Dscp = sendOptions.Dscp, Cancellation = cancellation, ResponseSource = responseSource }, sendOptions.Priority);
        ObjectDisposedException.ThrowIf(!isQueued, this);
        return responseSource.Task;
    }

    /// <summary>Immediately closes this connection, without waiting for anything in flight to finish. Replaces the old <c>Drop</c>.</summary>
    public void Dispose() => _ = Terminate(null, false);

    /// <summary>Cleanly closes this connection, waiting for it to finish before returning. Replaces the old <c>Disconnect</c>.</summary>
    /// <returns>A task that completes once this connection has closed.</returns>
    public async ValueTask DisposeAsync()
    {
        await Terminate(null, true);
        await Closed;
    }

    /// <summary>Attaches the channel this connection will run its handshake and traffic over. Must be called before connecting begins, so a concurrent <see cref="Dispose"/> always has something to abort.</summary>
    /// <param name="channel">The channel, not yet connected or handshaken.</param>
    internal void AttachChannel(IMsmtTlsChannel channel)
    {
        this.channel = channel;

        if (connectingCancellation.IsCancellationRequested)
        {
            channel.Abort(new MsmtConnectionClosedException());
        }
    }

    /// <summary>
    /// Completes what MSMT layers on top of a successful TLS handshake: an initiator in session mode
    /// negotiates its lifetime; an acceptor reads the first message, which decides whether this is a
    /// session, and either answers the negotiation or holds the message for delivery. Transitions this
    /// connection to <see cref="MsmtConnectionStatus.Connected"/> and starts its loops on success. On
    /// failure, throws instead of transitioning this connection itself - the caller (<see
    /// cref="MsmtConnector"/>) decides how to translate the failure and reports it through <see
    /// cref="FailToConnect"/>.
    /// </summary>
    /// <param name="remoteIdentity">The identity the remote peer presented during the handshake.</param>
    /// <param name="cancellation">Abandons the negotiation, typically because the handshake timeout passed.</param>
    /// <returns>A task that completes once this connection is connected and started.</returns>
    internal async Task CompleteHandshake(MsmtIdentity remoteIdentity, CancellationToken cancellation)
    {
        identity = remoteIdentity;

        if (settings.Role == MsmtConnectionRole.Initiator)
        {
            if (isSession)
            {
                await NegotiateAsInitiator(cancellation);
                keepAliveInterval = settings.KeepAliveMinInterval + ((settings.KeepAliveMaxInterval - settings.KeepAliveMinInterval) * keepAliveRandom.NextDouble());
            }
        }
        else
        {
            await ReadFirstMessage(cancellation);
        }

        if (Interlocked.CompareExchange(ref status, (int)MsmtConnectionStatus.Connected, (int)MsmtConnectionStatus.Connecting) != (int)MsmtConnectionStatus.Connecting)
        {
            // Disposed concurrently while negotiating; Terminate already ran and owns cleanup.
            return;
        }

        connectingResolved.TrySetResult(true);
        StartLoops();
    }

    /// <summary>Reports that the TCP connect or TLS handshake itself failed, before MSMT negotiation ever began.</summary>
    /// <param name="exception">Why it failed.</param>
    internal void FailToConnect(Exception exception) => _ = Terminate(exception, false);

    /// <summary>Cleanly closes this connection because the owner decided to, reporting <paramref name="reason"/>.</summary>
    /// <param name="reason">Why the connection is being closed, such as a <see cref="TimeoutException"/> for one that went unused.</param>
    /// <returns>A task that completes once the connection has closed.</returns>
    public async Task Evict(Exception reason)
    {
        await Terminate(reason, true);
        await Closed;
    }

    /// <summary>Sends a reachability check and waits for it to be echoed back.</summary>
    /// <param name="cancellation">Cancels the check, closing the connection.</param>
    /// <returns><see langword="true"/> if the remote side echoed the check.</returns>
    public async Task<bool> Ping(CancellationToken cancellation)
    {
        MsmtPendingSend send = new() { Payload = EmptyMemoryOwner.Instance, Flags = MsmtMessageFlags.ReachabilityCheck, Cancellation = cancellation, IsKeepAlive = true };
        MsmtFrame response = (await Exchange(send, _ => { }, cancellation)).Value;
        using (response.Payload)
        {
            return (response.Header.Flags & MsmtMessageFlags.ReachabilityCheck) == MsmtMessageFlags.ReachabilityCheck;
        }
    }

    /// <summary>
    /// Sends one message, the way every queued send is processed. Only one may be in flight at a time. A
    /// keep-alive, a reachability check, or a message that requested an acknowledgement waits for its
    /// reply; cancelling one already written, or its reply not arriving in time, closes the connection,
    /// since a late reply could then no longer be matched to anything. A plain send that never requested
    /// one completes as soon as it is written, without waiting for or expecting any reply at all.
    /// </summary>
    /// <param name="send">The message to send.</param>
    /// <param name="report">Reports the send's progress.</param>
    /// <param name="cancellation">Cancels the exchange.</param>
    /// <returns>The acknowledgement, whose payload the caller must dispose, or <see langword="null"/> for a send that never requested one, which never waits for a reply.</returns>
    /// <exception cref="MsmtConnectionClosedException">The connection had already closed, before anything was written.</exception>
    public async Task<MsmtFrame?> Exchange(MsmtPendingSend send, Action<MsmtSendStatus> report, CancellationToken cancellation)
    {
        if (Status == MsmtConnectionStatus.Disconnected)
        {
            throw new MsmtConnectionClosedException();
        }

        bool isAcknowledgementRequested = (send.Flags & MsmtMessageFlags.AcknowledgementRequestedOrGiven) == MsmtMessageFlags.AcknowledgementRequestedOrGiven;
        bool awaitsResponse = send.IsKeepAlive || isAcknowledgementRequested;
        ushort id = NextMessageId();
        PendingRequest request = new(id, new TaskCompletionSource<MsmtFrame>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (Interlocked.CompareExchange(ref currentRequest, request, null) is not null)
        {
            throw new InvalidOperationException("A message is already in flight on this connection.");
        }

        try
        {
            if (send.Dscp is { } dscp)
            {
                ApplyDscp(dscp);
            }

            report(MsmtSendStatus.Transmitting);
            MsmtHeader header = new() { Version = MsmtHeader.SupportedVersion, Flags = send.Flags, MessageId = id, Length = (uint)send.Payload.Memory.Length };
            await WriteFrame(header, send.Payload.Memory, cancellation);

            if (!awaitsResponse)
            {
                AfterExchange(send.IsKeepAlive);
                return null;
            }

            if (isAcknowledgementRequested)
            {
                report(MsmtSendStatus.PendingAcknowledgement);
            }

            MsmtFrame response = await request.Source.Task.WaitAsync(settings.ResponseTimeout ?? Timeout.InfiniteTimeSpan, cancellation);

            if ((response.Header.Flags & MsmtMessageFlags.InvalidPreambleOrModeUnsupported) != 0)
            {
                response.Payload.Dispose();
                throw new InvalidOperationException("The remote peer rejected the message: it did not accept the header or does not support this mode.");
            }

            AfterExchange(send.IsKeepAlive);
            return response;
        }
        catch (TimeoutException exception)
        {
            await Terminate(new TimeoutException("The remote peer did not acknowledge the message within the configured timeout.", exception), false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await Terminate(null, false);

            // Closing the connection cancels this exchange too, racing the exception that closing it set on the
            // request; the exception is the real reason, so it wins.
            if (request.Source.Task.IsFaulted)
            {
                ExceptionDispatchInfo.Capture(request.Source.Task.Exception!.InnerException!).Throw();
            }

            throw;
        }
        finally
        {
            Interlocked.CompareExchange(ref currentRequest, null, request);

            if (send.IsKeepAlive)
            {
                Volatile.Write(ref keepAliveQueued, 0);
            }
        }
    }

    /// <summary>Starts the loops that read, deliver, and, in a session, maintain this connection, and the queue that sends over it.</summary>
    private void StartLoops()
    {
        _ = Task.Run(ReaderLoop);
        _ = Task.Run(HandlerLoop);

        if (isSession)
        {
            _ = Task.Run(MaintenanceLoop);
        }

        if (settings.ProcessSends)
        {
            outbox.Start();
        }
    }

    private async Task NegotiateAsInitiator(CancellationToken cancellation)
    {
        byte[] lifetimePayload = Encoding.ASCII.GetBytes(((int)settings.SessionLifetime.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        MsmtHeader request = new()
        {
            Version = MsmtHeader.SupportedVersion,
            Flags = MsmtMessageFlags.SessionModeNegotiation | MsmtMessageFlags.MessageSuccess,
            MessageId = NextMessageId(),
            Length = (uint)lifetimePayload.Length,
        };

        await WriteFrame(request, lifetimePayload, cancellation);
        MsmtFrame reply = await ReadFrame(cancellation) ?? throw new IOException("The remote peer closed the connection during session negotiation.");

        using (reply.Payload)
        {
            if (!reply.Header.Acknowledges(request) || (reply.Header.Flags & MsmtMessageFlags.SessionModeNegotiation) == 0)
            {
                throw new InvalidOperationException("The server's session negotiation reply did not correspond to the sent request.");
            }

            if (reply.Header.Flags != MsmtMessageFlags.SessionModeAccepted)
            {
                throw new InvalidOperationException("The remote MSMT server rejected or does not support Session Mode.");
            }

            int agreedSeconds = int.Parse(Encoding.ASCII.GetString(reply.Payload.Memory.Span), CultureInfo.InvariantCulture);
            expiration = DateTime.UtcNow.AddSeconds(agreedSeconds);
        }
    }

    private async Task ReadFirstMessage(CancellationToken cancellation)
    {
        MsmtFrame first = await ReadFrame(cancellation) ?? throw new IOException("The remote peer closed the connection before sending a message.");

        if ((first.Header.Flags & MsmtMessageFlags.SessionModeNegotiation) != MsmtMessageFlags.SessionModeNegotiation)
        {
            if (settings.RequiresSession)
            {
                first.Payload.Dispose();
                await RejectAndClose(first.Header, MsmtMessageFlags.InvalidPreambleOrModeUnsupported, "The remote peer did not negotiate a session, and this server only accepts sessions.");
            }

            pendingFirstFrame = first;
            return;
        }

        int requestedSeconds;
        using (first.Payload)
        {
            if (settings.RejectsSession)
            {
                await RejectAndClose(first.Header, MsmtMessageFlags.SessionModeUnsupported, "The remote peer requested a session, which this endpoint does not support.");
            }

            try
            {
                requestedSeconds = int.Parse(Encoding.ASCII.GetString(first.Payload.Memory.Span), CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                requestedSeconds = 0;
            }
        }

        if (requestedSeconds <= 0)
        {
            await RejectAndClose(first.Header, MsmtMessageFlags.SessionModeUnsupported, "The remote peer proposed an invalid session lifetime.");
        }

        int agreedSeconds = Math.Min(requestedSeconds, (int)settings.MaximumSessionLifetime.TotalSeconds);
        await Respond(first.Header, MsmtMessageFlags.SessionModeAccepted, Encoding.ASCII.GetBytes(agreedSeconds.ToString(CultureInfo.InvariantCulture)), cancellation);
        isSession = true;
        expiration = DateTime.UtcNow.AddSeconds(agreedSeconds);
    }

    private async Task RejectAndClose(MsmtHeader request, MsmtMessageFlags flags, string message)
    {
        await Respond(request, flags, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        await channel!.Close();
        throw new NotSupportedException(message);
    }

    private async Task ReaderLoop()
    {
        try
        {
            if (pendingFirstFrame is { } first)
            {
                pendingFirstFrame = null;
                await Dispatch(first);
            }

            while (Status == MsmtConnectionStatus.Connected)
            {
                MsmtFrame? frame = await ReadFrame(lifetime.Token);
                if (frame is null)
                {
                    await Terminate(null, false);
                    return;
                }

                await Dispatch(frame.Value);
            }
        }
        catch (Exception exception)
        {
            await Terminate(Status == MsmtConnectionStatus.Disconnected ? null : exception, false);
        }
    }

    private async Task Dispatch(MsmtFrame frame)
    {
        MsmtHeader header = frame.Header;

        if (Volatile.Read(ref currentRequest) is { } request && header.MessageId == request.Id)
        {
            if (!request.Source.TrySetResult(frame))
            {
                frame.Payload.Dispose();
            }

            return;
        }

        if (settings.Role == MsmtConnectionRole.Acceptor && (header.Flags & MsmtMessageFlags.SessionModeNegotiation) == MsmtMessageFlags.SessionModeNegotiation)
        {
            frame.Payload.Dispose();
            await Respond(header, MsmtMessageFlags.SessionModeRejected, ReadOnlyMemory<byte>.Empty, lifetime.Token);
            return;
        }

        if ((header.Flags & MsmtMessageFlags.ReachabilityCheck) == MsmtMessageFlags.ReachabilityCheck)
        {
            using (frame.Payload)
            {
                await Respond(header, MsmtMessageFlags.ReachabilityCheck, frame.Payload.Memory, lifetime.Token);
            }

            return;
        }

        if (!settings.AcceptsRequests)
        {
            frame.Payload.Dispose();
            throw new InvalidOperationException("The remote peer sent a message this connection does not accept.");
        }

        // Not lifetime.Token: a frame that has already been fully read off the wire is committed to being
        // delivered, even if the connection starts closing (gracefully or not) in the meantime - closing
        // only completes this channel (see Terminate), it does not abandon what is already queued on it.
        await inbound.Writer.WriteAsync(frame, CancellationToken.None);
    }

    private async Task HandlerLoop()
    {
        try
        {
            // Not lifetime.Token, for the same reason as Dispatch's write: draining what was already queued
            // before the connection started closing must not be cut short by that same closing.
            await foreach (MsmtFrame frame in inbound.Reader.ReadAllAsync(CancellationToken.None))
            {
                Interlocked.Increment(ref inboundBusy);
                try
                {
                    await HandleRequest(frame);
                }
                finally
                {
                    Interlocked.Decrement(ref inboundBusy);
                }
            }
        }
        catch (OperationCanceledException) when (Status == MsmtConnectionStatus.Disconnected)
        {
        }
        catch (Exception exception)
        {
            await Terminate(exception, false);
        }
    }

    private async Task HandleRequest(MsmtFrame frame)
    {
        MsmtHeader request = frame.Header;
        bool isResponseRequested = (request.Flags & MsmtMessageFlags.AcknowledgementRequestedOrGiven) == MsmtMessageFlags.AcknowledgementRequestedOrGiven;
        MsmtReceiveResult? result;

        try
        {
            result = onReceived is null
                ? (isResponseRequested ? MsmtReceiveResult.Accept() : null)
                : await onReceived(this, frame.Payload.Memory, isResponseRequested);
        }
        finally
        {
            frame.Payload.Dispose();
        }

        if (result is { } decided)
        {
            if (!isResponseRequested)
            {
                throw new InvalidOperationException("A receiver must return null for a message that did not request an acknowledgement.");
            }

            MsmtMessageFlags flags = (decided.Success ? MsmtMessageFlags.MessageSuccess : MsmtMessageFlags.None) | MsmtMessageFlags.AcknowledgementRequestedOrGiven;
            using IMemoryOwner<byte> responsePayload = decided.Payload ?? EmptyMemoryOwner.Instance;
            await Respond(request, flags, responsePayload.Memory, lifetime.Token);
        }
        else if (isResponseRequested)
        {
            throw new InvalidOperationException("A receiver must return a non-null result for a message that requested an acknowledgement.");
        }

        long now = DateTime.UtcNow.Ticks;
        Interlocked.Exchange(ref lastInboundActivityTicks, now);
        Interlocked.Exchange(ref lastWireActivityTicks, now);

        if (isSession)
        {
            if (DateTime.UtcNow >= expiration)
            {
                BeginClose();
            }
        }
        else if (settings.Role == MsmtConnectionRole.Acceptor && Interlocked.Increment(ref messageCount) >= settings.RekeyLimit)
        {
            BeginClose();
        }
    }

    private async Task MaintenanceLoop()
    {
        try
        {
            using PeriodicTimer timer = new(maintenanceInterval);
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                Maintain();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Maintain()
    {
        if (Status != MsmtConnectionStatus.Connected)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;

        if (settings.Role == MsmtConnectionRole.Acceptor)
        {
            // The client closes first; this is the backstop for one that goes silent.
            if (now >= expiration + sessionExpiryGrace && IsIdle)
            {
                BeginClose();
            }

            return;
        }

        if (now >= expiration && IsIdle)
        {
            BeginClose();
        }
        else if (now - new DateTime(Interlocked.Read(ref lastWireActivityTicks), DateTimeKind.Utc) >= keepAliveInterval && Interlocked.CompareExchange(ref keepAliveQueued, 1, 0) == 0)
        {
            outbox.Enqueue(new MsmtPendingSend { Payload = EmptyMemoryOwner.Instance, Flags = MsmtMessageFlags.ReachabilityCheck, Cancellation = lifetime.Token, IsKeepAlive = true }, 0);
        }
    }

    private void AfterExchange(bool isKeepAlive)
    {
        Interlocked.Exchange(ref lastWireActivityTicks, DateTime.UtcNow.Ticks);
        int completed = Interlocked.Increment(ref messageCount);

        if (settings.Role != MsmtConnectionRole.Initiator)
        {
            return;
        }

        if (isSession)
        {
            if (!isKeepAlive && DateTime.UtcNow >= expiration)
            {
                BeginClose();
            }
        }
        else if (completed >= settings.RekeyLimit)
        {
            BeginClose();
        }
        else
        {
            try
            {
                channel!.Rekey();
            }
            catch (Exception exception)
            {
                _ = Terminate(exception, false);
            }
        }
    }

    private void BeginClose() => _ = Terminate(null, true);

    private async Task Terminate(Exception? reason, bool graceful)
    {
        MsmtConnectionStatus previous = (MsmtConnectionStatus)Interlocked.Exchange(ref status, (int)MsmtConnectionStatus.Disconnected);
        if (previous == MsmtConnectionStatus.Disconnected)
        {
            return;
        }

        closeReason = reason;
        connectingCancellation.Cancel();
        lifetime.Cancel();

        if (graceful && channel is not null)
        {
            try
            {
                await channel.Close();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
            }
        }
        else
        {
            channel?.Abort(reason);
        }

        Interlocked.Exchange(ref currentRequest, null)?.Source.TrySetException(reason ?? new MsmtConnectionClosedException());
        inbound.Writer.TryComplete();
        outbox.Dispose();

        if (previous == MsmtConnectionStatus.Connecting)
        {
            connectingResolved.TrySetResult(false);
        }

        try
        {
            onDisconnected?.Invoke(this, reason);
        }
        finally
        {
            closedSource.TrySetResult();
        }
    }

    private async Task<MsmtFrame?> ReadFrame(CancellationToken cancellation)
    {
        byte[] headerBuffer = new byte[MsmtHeader.Size];

        // Waiting for a message to start is idle, not a stall, so only the rest of it is timed.
        if (await channel!.Read(headerBuffer.AsMemory(0, 1), cancellation) == 0)
        {
            return null;
        }

        await ReadExact(headerBuffer.AsMemory(1), cancellation);
        MsmtHeader header = MsmtHeader.Read(headerBuffer);

        if (!header.IsWellFormed())
        {
            if (settings.Role == MsmtConnectionRole.Acceptor)
            {
                await RejectAndClose(header, MsmtMessageFlags.InvalidPreambleOrModeUnsupported, "The remote peer sent a malformed message header.");
            }

            throw new InvalidDataException("The remote peer sent a malformed message header.");
        }

        IMemoryOwner<byte> payload = MemoryPool<byte>.Shared.Rent((int)header.Length).Slice(0, (int)header.Length);

        try
        {
            await ReadExact(payload.Memory, cancellation);
        }
        catch
        {
            payload.Dispose();
            throw;
        }

        return new MsmtFrame(header, payload);
    }

    private async Task ReadExact(Memory<byte> buffer, CancellationToken cancellation)
    {
        using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        int read = 0;

        try
        {
            while (read < buffer.Length)
            {
                if (settings.StallTimeout is { } limit)
                {
                    stall.CancelAfter(limit);
                }

                int chunk = await channel!.Read(buffer[read..], stall.Token);
                if (chunk == 0)
                {
                    throw new IOException("The remote peer closed the connection in the middle of a message.");
                }

                read += chunk;
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("The remote peer stopped sending in the middle of a message within the configured stall timeout.");
        }
    }

    private async Task WriteFrame(MsmtHeader header, ReadOnlyMemory<byte> payload, CancellationToken cancellation)
    {
        byte[] headerBytes = new byte[MsmtHeader.Size];
        header.Write(headerBytes);

        await writeLock.WaitAsync(cancellation);
        try
        {
            await channel!.Write(headerBytes, cancellation);
            if (payload.Length > 0)
            {
                await channel.Write(payload, cancellation);
            }
        }
        finally
        {
            writeLock.Release();
        }
    }

    private Task Respond(MsmtHeader request, MsmtMessageFlags flags, ReadOnlyMemory<byte> payload, CancellationToken cancellation) =>
        WriteFrame(new MsmtHeader { Version = MsmtHeader.SupportedVersion, Flags = flags, MessageId = request.MessageId, Length = (uint)payload.Length }, payload, cancellation);

    private ushort NextMessageId() =>
        (ushort)((Random.Shared.Next(0, 32768) << 1) | (settings.Role == MsmtConnectionRole.Initiator ? 0 : 1));

    // Applied per send rather than once per connection, since a connection reused across sends may be asked
    // for different values. Marking is inherently best-effort per the ICD, so a platform that rejects it
    // doesn't fail the send.
    private void ApplyDscp(int dscp)
    {
        try
        {
            channel!.Socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, dscp << 2);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
        }
    }

    private sealed record PendingRequest(ushort Id, TaskCompletionSource<MsmtFrame> Source);
}
