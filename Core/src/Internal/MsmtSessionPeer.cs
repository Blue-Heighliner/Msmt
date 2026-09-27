namespace BlueHeighliner.Msmt.Internal;

/// <inheritdoc cref="IMsmtSessionPeer" />
/// <remarks>
/// Accepted and opened connections share one registry and one set of callbacks routed into this peer's
/// own event subjects: once established, either kind is a connection like any other, and both directions
/// can send to and receive from the caller freely. Every accepted connection negotiates a session or is
/// turned away. Unlike <see cref="MsmtMessagePeer"/>, this peer never disconnects a connection on its own -
/// only a timeout, a stall, or the caller explicitly ending it closes one - since managing session
/// connections is the caller's job. Tagged sends are tracked by the peer itself, not by a connection, so a
/// package outlives whatever connection carried it.
/// </remarks>
internal sealed class MsmtSessionPeer : IMsmtSessionPeer
{
    /// <summary>Creates a session peer with no connections, which does not listen until <see cref="StartListener"/> is called.</summary>
    /// <param name="options">The peer's credentials and connection-behavior settings.</param>
    /// <exception cref="ArgumentOutOfRangeException">A timeout or interval in <paramref name="options"/> is not positive, or the keep-alive minimum exceeds its maximum.</exception>
    public MsmtSessionPeer(MsmtSessionPeerOptions options)
    {
        options.Validate();
        this.options = options;
        connector = new MsmtConnector(options, options.RequireFullyQualifiedHostname);
    }

    private readonly MsmtSessionPeerOptions options;
    private readonly IMsmtConnector connector;
    private readonly IMsmtPackageTracker packageTracker = new MsmtPackageTracker();
    private readonly ConcurrentDictionary<MsmtConnection, byte> connections = new();
    private readonly MsmtEventSubject<IMsmtConnection> connected = new();
    private readonly MsmtEventSubject<MsmtDisconnection> disconnected = new();
    private readonly MsmtEventSubject<MsmtPackageChange> packageChanged = new();
    private readonly CancellationTokenSource disposalCancellation = new();

    private IMsmtListener? listener;
    private int isDisposed;

    /// <inheritdoc />
    public IObservable<IMsmtConnection> Connected => connected;

    /// <inheritdoc />
    public IObservable<MsmtDisconnection> Disconnected => disconnected;

    /// <inheritdoc />
    public IObservable<MsmtPackageChange> PackageChanged => packageChanged;

    /// <inheritdoc />
    public MsmtSessionReceiver? Receiver { get; set; }

    /// <inheritdoc />
    public MsmtTarget? Listener =>
        listener?.LocalEndPoint is { } endPoint
            ? new MsmtTarget { Host = endPoint.Address.ToString(), Port = endPoint.Port }
            : null;

    /// <inheritdoc />
    public IReadOnlyList<IMsmtConnection> Connections => [.. connections.Keys];

    /// <inheritdoc />
    public IReadOnlyList<IMsmtPackage> Packages => packageTracker.GetActivePackages();

    private bool IsDisposed => Volatile.Read(ref isDisposed) != 0;

    /// <inheritdoc />
    public void StartListener(int port = 0, string host = "0.0.0.0")
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        StopListener();

        MsmtListener newListener = new();
        try
        {
            newListener.Start(host, port, OnAccepted, disposalCancellation.Token);
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
    public IMsmtConnection Connect(MsmtNameTarget target)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        MsmtConnection connection = connector.Dial(target, new MsmtConnectionSettings
        {
            Role = MsmtConnectionRole.Initiator,
            Mode = MsmtConnectionMode.Session,
            AcceptsRequests = true,
            HandshakeTimeout = options.HandshakeTimeout,
            StallTimeout = options.StallTimeout,
            ResponseTimeout = options.ResponseTimeout,
            SessionLifetime = options.SessionLifetime,
            KeepAliveMinInterval = options.KeepAliveMinInterval,
            KeepAliveMaxInterval = options.KeepAliveMaxInterval,
        }, packageTracker, OnReceived, OnPackageChanged, OnDisconnected);

        Track(connection);

        // A peer disposed concurrently would otherwise never close a connection it just started.
        if (IsDisposed)
        {
            connection.Dispose();
        }

        return connection;
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

        foreach (MsmtConnection connection in connections.Keys)
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

        if (listener is { } stopping)
        {
            listener = null;
            await stopping.DisposeAsync();
        }

        await Task.WhenAll(connections.Keys.Select(connection => connection.DisposeAsync().AsTask()));
    }

    private async Task OnAccepted(Socket socket, CancellationToken cancellation)
    {
        MsmtConnection connection = connector.Accept(socket, new MsmtConnectionSettings
        {
            Role = MsmtConnectionRole.Acceptor,
            Mode = MsmtConnectionMode.Session,
            AcceptsRequests = true,
            RequiresSession = true,
            HandshakeTimeout = options.HandshakeTimeout,
            StallTimeout = options.StallTimeout,
            ResponseTimeout = options.ResponseTimeout,
            MaximumSessionLifetime = options.MaximumSessionLifetime,
        }, packageTracker, OnReceived, OnPackageChanged, OnDisconnected);

        Track(connection);

        bool isConnected;
        try
        {
            isConnected = await connection.Wait(cancellation);
        }
        catch (OperationCanceledException)
        {
            // The peer was disposed while this accept was still connecting; the connection is disposed too,
            // which reports it through the same onDisconnected callback every other ending uses.
            connection.Dispose();
            return;
        }

        if (isConnected)
        {
            try
            {
                connected.Publish(connection);
            }
            catch (Exception exception)
            {
                // A throwing subscriber leaves the connection unusable; close it gracefully - not an abrupt
                // abort, which could race the negotiation reply already written to the wire moments earlier
                // - and report the failure through Disconnected like any other connection that never became
                // usable.
                await connection.Evict(exception);
            }
        }
    }

    private void Track(MsmtConnection connection) => connections[connection] = 0;

    private ValueTask<MsmtReceiveResult?> OnReceived(MsmtConnection connection, ReadOnlyMemory<byte> payload, bool isResponseRequested) =>
        Receiver is null ? new ValueTask<MsmtReceiveResult?>(isResponseRequested ? MsmtReceiveResult.Accept() : null) : Receiver(connection, payload, isResponseRequested);

    private void OnPackageChanged(MsmtConnection connection, MsmtPackageChange args) =>
        packageChanged.Publish(args with { Connection = connection });

    private void OnDisconnected(MsmtConnection connection, Exception? reason)
    {
        connections.TryRemove(connection, out _);
        disconnected.Publish(new MsmtDisconnection { Connection = connection, Exception = reason });
    }
}
