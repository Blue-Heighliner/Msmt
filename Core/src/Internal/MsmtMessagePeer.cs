namespace BlueHeighliner.Msmt.Internal;

/// <inheritdoc cref="IMsmtMessagePeer" />
/// <remarks>
/// Sends go through one <see cref="MsmtTargetSender"/> per remote target, created on demand, and messages
/// arrive on the connections the peer's listener accepts; neither kind of connection is exposed. Idle time
/// and connection count limits (see <see cref="MsmtMessagePeerOptions.MaxIdleTime"/> and <see
/// cref="MsmtMessagePeerOptions.MaxConnectionCount"/>) apply to both, counted independently, by one sweep
/// here rather than by each connection, and never interrupt a message in progress. Tagged sends are
/// tracked by the peer itself, not by a per-target sender, so their packages outlive its eviction.
/// </remarks>
internal sealed class MsmtMessagePeer : IMsmtMessagePeer
{
    /// <summary>
    /// Creates a peer, immediately starting its background eviction loop. Call <see cref="StartListener"/>
    /// separately to also start listening.
    /// </summary>
    /// <param name="options">Shared credentials and connection-behavior defaults for this peer.</param>
    /// <exception cref="ArgumentOutOfRangeException">A timeout in <paramref name="options"/> is not positive.</exception>
    public MsmtMessagePeer(MsmtMessagePeerOptions options)
    {
        options.Validate();
        this.options = options;
        packageChanged = new MsmtEventSubject<MsmtPackageChange>(Report);
        connector = new MsmtConnector(options, options.RequireFullyQualifiedHostname);
        eviction = new MsmtEviction(options.MaxIdleTime, options.MaxConnectionCount);
        CancellationToken cancellation = disposalCancellation.Token;
        evictionLoop = Task.Run(() => EvictionLoop(cancellation));
    }

    private readonly MsmtMessagePeerOptions options;
    private readonly IMsmtConnector connector;
    private readonly IMsmtEviction eviction;
    private readonly IMsmtPackageTracker packageTracker = new MsmtPackageTracker();
    private readonly ConcurrentDictionary<(string Host, int Port), MsmtTargetSender> senders = new();
    private readonly ConcurrentDictionary<MsmtConnection, byte> accepted = new();
    private readonly MsmtEventSubject<Exception> exceptions = new();
    private readonly MsmtEventSubject<MsmtPackageChange> packageChanged;
    private readonly CancellationTokenSource disposalCancellation = new();
    private readonly TimeSpan evictionCheckInterval = TimeSpan.FromSeconds(1);
    private readonly Task evictionLoop;

    private IMsmtListener? listener;
    private int isDisposed;

    /// <inheritdoc />
    public IObservable<MsmtPackageChange> PackageChanged => packageChanged;

    /// <inheritdoc />
    public IObservable<Exception> Exceptions => exceptions;

    /// <inheritdoc />
    public MsmtMessageReceiver? Receiver { get; set; }

    /// <inheritdoc />
    public MsmtTarget? Listener =>
        listener?.LocalEndPoint is { } endPoint
            ? new MsmtTarget { Host = endPoint.Address.ToString(), Port = endPoint.Port }
            : null;

    /// <inheritdoc />
    public IReadOnlyList<IMsmtPackage> Packages => packageTracker.GetActivePackages();

    /// <summary>Gets how many per-target senders are currently pooled. Exposed for diagnostics and testing.</summary>
    internal int SenderCount => senders.Count;

    /// <summary>Gets how many accepted connections are currently open. Exposed for diagnostics and testing.</summary>
    internal int AcceptedCount => accepted.Count;

    private bool IsDisposed => Volatile.Read(ref isDisposed) != 0;

    /// <inheritdoc />
    public void StartListener(int port = 0, string host = "0.0.0.0")
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        StopListener();

        MsmtListener newListener = new();
        try
        {
            newListener.Start(host, port, OnAccepted, Report, disposalCancellation.Token);
        }
        catch
        {
            newListener.Dispose();
            throw;
        }

        listener = newListener;
    }

    /// <inheritdoc />
    public void StopListener()
    {
        listener?.Dispose();
        listener = null;
    }

    /// <inheritdoc />
    public void Send(MsmtNameTarget target, IMemoryOwner<byte> payload, MsmtSendOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        // Validated before ReserveSender so an oversized payload never opens a connection it can't use.
        MsmtProtocol.ValidatePayloadLength(payload.Memory.Length, nameof(payload));
        MsmtSendOptions sendOptions = options ?? new MsmtSendOptions();
        MsmtTargetSender sender = ReserveSender(target);

        try
        {
            bool isQueued = sender.Enqueue(new MsmtPendingSend { Payload = payload, Flags = MsmtMessageFlags.None, Tag = sendOptions.Tag, Dscp = sendOptions.Dscp, Cancellation = CancellationToken.None }, sendOptions.Priority);
            ObjectDisposedException.ThrowIf(!isQueued, this);
        }
        finally
        {
            sender.ReleaseReservation();
        }
    }

    /// <inheritdoc />
    public Task<MsmtResponse> Request(MsmtNameTarget target, IMemoryOwner<byte> payload, MsmtSendOptions? options = null, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        MsmtProtocol.ValidatePayloadLength(payload.Memory.Length, nameof(payload));
        MsmtSendOptions sendOptions = options ?? new MsmtSendOptions();
        TaskCompletionSource<MsmtResponse> responseSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MsmtTargetSender sender = ReserveSender(target);

        try
        {
            bool isQueued = sender.Enqueue(new MsmtPendingSend { Payload = payload, Flags = MsmtMessageFlags.AcknowledgementRequestedOrGiven, Tag = sendOptions.Tag, Dscp = sendOptions.Dscp, Cancellation = cancellation, ResponseSource = responseSource }, sendOptions.Priority);
            ObjectDisposedException.ThrowIf(!isQueued, this);
        }
        finally
        {
            sender.ReleaseReservation();
        }

        return responseSource.Task;
    }

    /// <inheritdoc />
    public IMsmtPackage? GetPackage(object tag) => packageTracker.GetPackage(tag);

    /// <summary>Immediately abandons this peer: stops listening and closes every connection, without waiting for any of them to finish.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref isDisposed, 1) != 0)
        {
            return;
        }

        disposalCancellation.Cancel();
        StopListener();

        foreach (MsmtTargetSender sender in senders.Values)
        {
            sender.Dispose();
        }

        foreach (MsmtConnection connection in accepted.Keys)
        {
            connection.Dispose();
        }
    }

    /// <summary>Cleanly shuts this peer down: stops listening, then closes every connection and waits for each to finish.</summary>
    /// <returns>A task that completes once the shutdown has finished.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref isDisposed, 1) != 0)
        {
            return;
        }

        await disposalCancellation.CancelAsync();

        try
        {
            await evictionLoop;
        }
        catch (OperationCanceledException)
        {
        }

        // Closed before the listener: a still-open connection's reader on the other side may only stop
        // once this side closes it, so closing our own outgoing ones first avoids outliving them.
        foreach (MsmtTargetSender sender in senders.Values)
        {
            await sender.DisposeAsync();
        }

        if (listener is { } stopping)
        {
            listener = null;
            await stopping.DisposeAsync();
        }

        await Task.WhenAll(accepted.Keys.Select(connection => connection.DisposeAsync().AsTask()));
    }

    private MsmtConnectionSettings SenderSettings() => new()
    {
        Role = MsmtConnectionRole.Initiator,
        Mode = MsmtConnectionMode.Requesting,
        HandshakeTimeout = options.HandshakeTimeout,
        StallTimeout = options.StallTimeout,
        ResponseTimeout = options.ResponseTimeout,
        RekeyLimit = options.RekeyLimit,
        ProcessSends = false,
    };

    private MsmtTargetSender ReserveSender(MsmtNameTarget target)
    {
        (string Host, int Port) key = (target.Host, target.Port);

        while (true)
        {
            MsmtTargetSender sender = senders.GetOrAdd(key, _ => new MsmtTargetSender(target, connector, SenderSettings(), packageTracker, args => packageChanged.Publish(args)));

            if (sender.TryReserve())
            {
                // A sender created after disposal swept every sender would otherwise never be disposed.
                if (IsDisposed)
                {
                    sender.ReleaseReservation();
                    sender.Dispose();
                    throw new ObjectDisposedException(GetType().FullName);
                }

                return sender;
            }

            // Retired concurrently by eviction; replace it and retry.
            senders.TryRemove(KeyValuePair.Create(key, sender));
        }
    }

    private async Task OnAccepted(Socket socket, CancellationToken cancellation)
    {
        MsmtConnection connection;
        try
        {
            connection = await connector.AcceptAndWait(socket, new MsmtConnectionSettings
            {
                Role = MsmtConnectionRole.Acceptor,
                Mode = MsmtConnectionMode.Requesting,
                AcceptsRequests = true,
                RejectsSession = true,
                HandshakeTimeout = options.HandshakeTimeout,
                StallTimeout = options.StallTimeout,
                ResponseTimeout = options.ResponseTimeout,
                RekeyLimit = options.RekeyLimit,
            }, tracker: null, cancellation, OnReceived, onDisconnected: OnAcceptedDisconnected);
        }
        catch (Exception exception)
        {
            // Reported here rather than on a connection, since the peer exposes none; a peer that is being
            // disposed abandons handshakes still in progress on purpose, which is not worth reporting.
            if (!cancellation.IsCancellationRequested)
            {
                Report(exception);
            }

            return;
        }

        accepted[connection] = 0;
    }

    private void OnReceived(MsmtConnection connection, IMemoryOwner<byte> payload, IMsmtResponder? responder)
    {
        if (Receiver is null)
        {
            payload.Dispose();
            responder?.Accept();
            return;
        }

        try
        {
            Receiver(connection.Remote, connection.Identity!, payload, responder);
        }
        catch (Exception exception)
        {
            Report(exception);
        }
    }

    private void Report(Exception exception)
    {
        try
        {
            exceptions.Publish(exception);
        }
        catch (Exception)
        {
            // A subscriber to the exceptions themselves throwing has nowhere left to be reported.
        }
    }

    private void OnAcceptedDisconnected(MsmtConnection connection, Exception? reason) => accepted.TryRemove(connection, out _);

    private async Task EvictionLoop(CancellationToken cancellation)
    {
        using PeriodicTimer timer = new(evictionCheckInterval);
        while (await timer.WaitForNextTickAsync(cancellation))
        {
            try
            {
                await Sweep();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failure evicting one connection must not end eviction for the life of the peer.
                Report(exception);
            }
        }
    }

    private async Task Sweep()
    {
        packageTracker.RemoveExpired();

        foreach ((MsmtTargetSender sender, _, TimeSpan minIdleTime) in eviction.SelectVictims<MsmtTargetSender>([.. senders.Values]))
        {
            // Re-checked, since a send may have been queued on it since it was selected above.
            if (sender.TryRetire(minIdleTime))
            {
                senders.TryRemove(KeyValuePair.Create((sender.Target.Host, sender.Target.Port), sender));
                await sender.DisposeAsync();
            }
        }

        foreach ((MsmtConnection connection, Exception reason, TimeSpan minIdleTime) in eviction.SelectVictims<MsmtConnection>([.. accepted.Keys]))
        {
            if (connection.IsIdle && DateTime.UtcNow - connection.LastActivityUtc >= minIdleTime)
            {
                await connection.Evict(reason);
            }
        }
    }
}
