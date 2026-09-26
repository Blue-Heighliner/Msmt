namespace BlueHeighliner.Msmt.Internal;

/// <inheritdoc cref="IMsmtPeer" />
/// <remarks>
/// Idle time and connection count limits (see <see cref="MsmtOptions.MaxIdleTime"/> and <see
/// cref="MsmtOptions.MaxConnectionCount"/>) apply symmetrically to both the outgoing links this peer
/// creates on demand in <see cref="Send"/> and the incoming links its internal <see cref="MsmtServer"/>
/// receiver accepts, counted independently per direction, and never interrupt a link with a
/// send/message cycle currently in progress. Both are enforced here, by one sweep over the pool, rather
/// than by each connection. Tagged sends are tracked by the peer itself, not by the pooled client that
/// carried them, so their packages outlive that client's eviction.
/// </remarks>
internal sealed class MsmtPeer : IMsmtPeer
{
    /// <summary>
    /// Creates a peer, immediately starting its background on-demand connection eviction loop. Call <see
    /// cref="StartListener"/> separately to also start listening.
    /// </summary>
    /// <param name="options">Shared credentials and connection-behavior defaults for this peer.</param>
    /// <exception cref="ArgumentOutOfRangeException">A timeout or interval in <paramref name="options"/> is not positive, or <see cref="MsmtOptions.KeepAliveMinInterval"/> exceeds <see cref="MsmtOptions.KeepAliveMaxInterval"/>.</exception>
    public MsmtPeer(MsmtOptions options)
    {
        ValidateOptions(options);
        this.options = options;
        CancellationToken cancellation = disposalCancellation.Token;
        evictionLoop = Task.Run(() => EvictionLoop(cancellation));
    }

    private readonly MsmtOptions options;
    private readonly ConcurrentDictionary<(string Host, int Port), MsmtConnection> connections = new();
    private readonly ConcurrentDictionary<IMsmtConnection, byte> activeConnections = new();
    private readonly IMsmtPackageTracker packageTracker = new MsmtPackageTracker();
    private readonly CancellationTokenSource disposalCancellation = new();
    private readonly TimeSpan evictionCheckInterval = TimeSpan.FromSeconds(1);
    private readonly int maxDroppedSenderRetries = 2;
    private readonly Task evictionLoop;
    private readonly MsmtEventSubject<MsmtLinkingEventArgs> linking = new();
    private readonly MsmtEventSubject<MsmtLinkedEventArgs> linked = new();
    private readonly MsmtEventSubject<MsmtLinkFailedEventArgs> linkFailed = new();
    private readonly MsmtEventSubject<MsmtReceivedEventArgs> received = new();
    private readonly MsmtEventSubject<MsmtUnlinkedEventArgs> unlinked = new();
    private readonly MsmtEventSubject<MsmtPackageChangedEventArgs> packageChanged = new();
    private readonly MsmtEventSubject<MsmtConnectedEventArgs> connected = new();
    private readonly MsmtEventSubject<MsmtDisconnectedEventArgs> disconnected = new();

    private MsmtServer? server;
    private int disposed;

    /// <inheritdoc />
    public IObservable<MsmtLinkingEventArgs> Linking => linking;

    /// <inheritdoc />
    public IObservable<MsmtLinkedEventArgs> Linked => linked;

    /// <inheritdoc />
    public IObservable<MsmtLinkFailedEventArgs> LinkFailed => linkFailed;

    /// <inheritdoc />
    public IObservable<MsmtReceivedEventArgs> Received => received;

    /// <inheritdoc />
    public IObservable<MsmtUnlinkedEventArgs> Unlinked => unlinked;

    /// <inheritdoc />
    public IObservable<MsmtPackageChangedEventArgs> PackageChanged => packageChanged;

    /// <inheritdoc />
    public IObservable<MsmtConnectedEventArgs> Connected => connected;

    /// <inheritdoc />
    public IObservable<MsmtDisconnectedEventArgs> Disconnected => disconnected;

    /// <inheritdoc />
    public MsmtTarget? Listener =>
        server?.LocalEndPoint is IPEndPoint endPoint
            ? new MsmtTarget { Host = endPoint.Address.ToString(), Port = endPoint.Port }
            : null;

    /// <inheritdoc />
    public IReadOnlyList<IMsmtConnection> ActiveConnections => [.. activeConnections.Keys];

    /// <inheritdoc />
    public IReadOnlyList<IMsmtPackage> Packages => packageTracker.GetActivePackages();

    /// <summary>Gets how many outgoing connections are currently pooled, whether or not each has an open link. Exposed for diagnostics and testing.</summary>
    internal int SenderCount => EnumerateSenders().Count();

    private bool IsDisposed => Volatile.Read(ref disposed) != 0;

    /// <inheritdoc />
    public void StartListener(int port = 0, string host = "0.0.0.0")
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        StopListener();

        MsmtServer newServer = new();
        newServer.Linking += (_, args) =>
        {
            AttachIncomingLink((MsmtServerConnection)args.Link);
            linking.Publish(args);
        };
        newServer.Linked += (_, args) => RaiseLinked(args);
        newServer.LinkFailed += (_, args) =>
        {
            DetachIncomingLink((MsmtServerConnection)args.Link);
            RaiseLinkFailed(args);
        };
        newServer.Received += (_, args) => received.Publish(args);
        newServer.Unlinked += (_, args) =>
        {
            DetachIncomingLink((MsmtServerConnection)args.Link);
            RaiseUnlinked(args);
        };
        newServer.PackageChanged += (_, args) => packageChanged.Publish(args);

        try
        {
            newServer.Host(new MsmtHostOptions
            {
                Host = host,
                Port = port,
                Credentials = options.Credentials,
                SupportsSessionMode = options.SupportsSessionMode,
                MaximumSessionLifetime = options.MaximumSessionLifetime,
                RequireFullyQualifiedHostname = options.RequireFullyQualifiedHostname,
                RekeyLimit = options.RekeyLimit,
                HandshakeTimeout = options.HandshakeTimeout,
                StallTimeout = options.StallTimeout,
                TcpKeepAliveTime = options.TcpKeepAliveTime,
            });
        }
        catch
        {
            newServer.Dispose();
            throw;
        }

        server = newServer;
    }

    /// <inheritdoc />
    public void StopListener()
    {
        server?.Dispose();
        server = null;
    }

    /// <inheritdoc />
    public void Send(MsmtNameTarget target, IMemoryOwner<byte> payload, MsmtSendOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        // Validated before ReserveSender so an oversized payload never opens a connection it can't use.
        MsmtProtocol.ValidatePayloadLength(payload.Memory.Length, nameof(payload));

        for (int attempt = 0; ; attempt++)
        {
            MsmtClient sender = ReserveSender(target);

            try
            {
                sender.Send(payload, options);
                return;
            }
            catch (ObjectDisposedException) when (attempt < maxDroppedSenderRetries && !IsDisposed)
            {
                // The caller dropped or disconnected this connection between reserving it and queuing on
                // it. It has already left the pool, so a retry gets a fresh one; the payload was not taken.
            }
            finally
            {
                sender.ReleaseReservation();
            }
        }
    }

    /// <inheritdoc />
    public async Task<MsmtResponse> Request(MsmtNameTarget target, IMemoryOwner<byte> payload, MsmtSendOptions? options = null, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        MsmtProtocol.ValidatePayloadLength(payload.Memory.Length, nameof(payload));

        Task<MsmtResponse> response;

        for (int attempt = 0; ; attempt++)
        {
            MsmtClient sender = ReserveSender(target);

            try
            {
                response = sender.Request(payload, options, cancellation);
                break;
            }
            catch (ObjectDisposedException) when (attempt < maxDroppedSenderRetries && !IsDisposed)
            {
                // See Send: the dropped connection has left the pool, and the payload was not taken.
            }
            finally
            {
                sender.ReleaseReservation();
            }
        }

        return await response;
    }

    /// <inheritdoc />
    public IMsmtPackage? GetPackage(object tag) => packageTracker.GetPackage(tag);

    /// <inheritdoc />
    public IMsmtConnection? GetActiveConnection(MsmtTarget target)
    {
        if (!connections.TryGetValue((target.Host, target.Port), out MsmtConnection? connection))
        {
            return null;
        }

        return (connection.Sender is not null) != (connection.Receiver is not null) ? connection : null;
    }

    /// <inheritdoc />
    public async Task<bool> Test(MsmtNameTarget target, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        using TcpClient tcpClient = new();
        using MsmtWatchdog watchdog = new(tcpClient.Close);

        // Neither BouncyCastle's blocking handshake nor its TLS stream honors a cancellation token once
        // blocked, so closing the socket is the only way cancellation can take effect promptly.
        using CancellationTokenRegistration closeOnCancellation = cancellation.Register(tcpClient.Close);

        try
        {
            TlsClientProtocol protocol;
            using (watchdog.Guard(options.HandshakeTimeout, false))
            {
                await tcpClient.ConnectAsync(target.Host, target.Port, cancellation);
                MsmtProtocol.ApplyTcpKeepAlive(tcpClient.Client, options.TcpKeepAliveTime);

                protocol = new(new MsmtWatchedStream(tcpClient.GetStream(), watchdog));
                MsmtTlsClient tlsClient = new(new MsmtConnectOptions { Target = target, Credentials = options.Credentials });
                await Task.Run(() => protocol.Connect(tlsClient), cancellation);
            }

            try
            {
                return await ExchangeReachabilityCheck(protocol.Stream, watchdog, cancellation);
            }
            finally
            {
                try
                {
                    protocol.Close();
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                }
            }
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or TimeoutException) && cancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException("The reachability test was cancelled.", exception, cancellation);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or TimeoutException) && watchdog.HasExpired)
        {
            throw new TimeoutException("The remote peer did not respond within the configured timeout.", exception);
        }
    }

    /// <summary>
    /// Immediately abandons this peer: stops accepting new connections, signals every open link - both
    /// accepted and on-demand - to close, without waiting for any of them to finish. Prefer <see
    /// cref="DisposeAsync"/> for a clean, awaited shutdown; use this only when an immediate, non-blocking
    /// teardown is required.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        disposalCancellation.Cancel();
        StopListener();

        foreach (MsmtClient sender in EnumerateSenders())
        {
            sender.Dispose();
        }

        disposalCancellation.Dispose();
    }

    /// <summary>
    /// Cleanly shuts this peer down: stops accepting new connections and waits for every open link - both
    /// accepted and on-demand - to finish. Prefer this over <see cref="Dispose"/> whenever an awaited
    /// shutdown is acceptable.
    /// </summary>
    /// <returns>A task that completes once the shutdown has finished.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
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

        // Closed before the server: a still-open link's read loop on the other side may only unblock once
        // this side closes it, so closing our own on-demand links first avoids this peer outliving links it
        // could have released earlier.
        foreach (MsmtClient sender in EnumerateSenders())
        {
            await sender.DisposeAsync();
        }

        if (server is not null)
        {
            await server.DisposeAsync();
            server = null;
        }

        disposalCancellation.Dispose();
    }

    private void ValidateOptions(MsmtOptions candidate)
    {
        foreach ((string name, TimeSpan? value) in new (string, TimeSpan?)[]
        {
            (nameof(MsmtOptions.MaxIdleTime), candidate.MaxIdleTime),
            (nameof(MsmtOptions.HandshakeTimeout), candidate.HandshakeTimeout),
            (nameof(MsmtOptions.StallTimeout), candidate.StallTimeout),
            (nameof(MsmtOptions.ResponseTimeout), candidate.ResponseTimeout),
            (nameof(MsmtOptions.TcpKeepAliveTime), candidate.TcpKeepAliveTime),
            (nameof(MsmtOptions.KeepAliveMinInterval), candidate.KeepAliveMinInterval),
            (nameof(MsmtOptions.KeepAliveMaxInterval), candidate.KeepAliveMaxInterval),
        })
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(candidate), value, $"{name} must be positive, or null where it can be disabled.");
            }
        }

        if (candidate.KeepAliveMinInterval > candidate.KeepAliveMaxInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(candidate), candidate.KeepAliveMinInterval, "KeepAliveMinInterval must not exceed KeepAliveMaxInterval.");
        }
    }

    private IEnumerable<MsmtClient> EnumerateSenders() =>
        connections.Values.Select(connection => connection.SenderEngine).OfType<MsmtClient>();

    private async Task<bool> ExchangeReachabilityCheck(Stream stream, MsmtWatchdog watchdog, CancellationToken cancellation)
    {
        MsmtHeader requestHeader = new()
        {
            Version = MsmtHeader.SupportedVersion,
            Flags = MsmtMessageFlags.ReachabilityCheck,
            MessageId = MsmtProtocol.GenerateMessageId(),
            Length = 0,
        };

        byte[] headerBuffer = new byte[MsmtHeader.Size];
        requestHeader.Write(headerBuffer);

        using (watchdog.Guard(options.StallTimeout, true))
        {
            await stream.WriteAsync(headerBuffer, cancellation);
        }

        MsmtHeader responseHeader;
        using (watchdog.Guard(options.ResponseTimeout, false))
        {
            await MsmtProtocol.ReadExact(stream, headerBuffer, cancellation);
            responseHeader = MsmtHeader.Read(headerBuffer);
        }

        if (!responseHeader.IsWellFormed())
        {
            return false;
        }

        if (responseHeader.Length > 0)
        {
            using (watchdog.Guard(options.StallTimeout, true))
            {
                await MsmtProtocol.ReadExact(stream, new byte[responseHeader.Length], cancellation);
            }
        }

        return responseHeader.Acknowledges(requestHeader) && (responseHeader.Flags & MsmtMessageFlags.ReachabilityCheck) == MsmtMessageFlags.ReachabilityCheck;
    }

    private void RaiseLinked(MsmtLinkedEventArgs args)
    {
        linked.Publish(args);
        CheckConnected(args.Link);
    }

    private void RaiseLinkFailed(MsmtLinkFailedEventArgs args)
    {
        linkFailed.Publish(args);
    }

    private void RaiseUnlinked(MsmtUnlinkedEventArgs args)
    {
        unlinked.Publish(args);
        CheckDisconnected(args.Link);
    }

    private void CheckConnected(IMsmtLink link)
    {
        if (OtherLink(link) is null)
        {
            activeConnections[link.Connection] = 0;
            connected.Publish(new MsmtConnectedEventArgs { Connection = link.Connection });
        }
    }

    private void CheckDisconnected(IMsmtLink link)
    {
        if (OtherLink(link) is null)
        {
            activeConnections.TryRemove(link.Connection, out _);
            disconnected.Publish(new MsmtDisconnectedEventArgs { Connection = link.Connection });
        }
    }

    private IMsmtLink? OtherLink(IMsmtLink link) =>
        link.Kind == MsmtLinkType.Sender ? link.Connection.Receiver : link.Connection.Sender;

    private void AttachIncomingLink(MsmtServerConnection link)
    {
        (string Host, int Port) key = (link.Target.Host, link.Target.Port);
        MsmtConnection connection = connections.GetOrAdd(key, _ => new MsmtConnection(link.Target));

        while (!connection.TryAttachReceiver(link))
        {
            connection = ReplaceRetiredConnection(key, connection);
        }
    }

    private void DetachIncomingLink(MsmtServerConnection link)
    {
        (string Host, int Port) key = (link.Target.Host, link.Target.Port);
        if (connections.TryGetValue(key, out MsmtConnection? connection))
        {
            connection.RemoveReceiver(link);
            RemoveConnectionIfEmpty(key, connection);
        }
    }

    /// <summary>
    /// Gets or creates <paramref name="target"/>'s outgoing engine, reserved (see <see
    /// cref="MsmtClient.Reserve"/>) so the caller must release it once its send is queued.
    /// </summary>
    /// <param name="target">The remote peer to send to.</param>
    /// <returns>The reserved engine.</returns>
    /// <exception cref="ObjectDisposedException">This peer was disposed concurrently.</exception>
    private MsmtClient ReserveSender(MsmtNameTarget target)
    {
        (string Host, int Port) key = (target.Host, target.Port);
        MsmtConnection connection = connections.GetOrAdd(key, _ => new MsmtConnection(new MsmtTarget { Host = target.Host, Port = target.Port }));
        MsmtClient? sender;

        while ((sender = connection.ReserveSender(() => CreateSender(connection, key, target))) is null)
        {
            connection = ReplaceRetiredConnection(key, connection);
        }

        // A sender created after disposal swept every connection would otherwise never be disposed.
        if (IsDisposed)
        {
            sender.ReleaseReservation();
            sender.Dispose();
            throw new ObjectDisposedException(GetType().FullName);
        }

        return sender;
    }

    /// <summary>Removes <paramref name="retired"/> from the registry, if still there, and gets or creates its replacement.</summary>
    /// <param name="key">The registry key <paramref name="retired"/> was found under.</param>
    /// <param name="retired">A connection that refused an attach because it was retired concurrently.</param>
    /// <returns>The connection now registered under <paramref name="key"/>.</returns>
    private MsmtConnection ReplaceRetiredConnection((string Host, int Port) key, MsmtConnection retired)
    {
        connections.TryRemove(KeyValuePair.Create(key, retired));
        return connections.GetOrAdd(key, _ => new MsmtConnection(retired.Target));
    }

    private MsmtClient CreateSender(MsmtConnection connection, (string Host, int Port) key, MsmtNameTarget target)
    {
        MsmtClient client = new(packageTracker);

        // Built and only started below: ConcurrentDictionary offers no way to run a side-effecting factory
        // exactly once, so this factory only ever runs under MsmtConnection.ReserveSender's own lock,
        // guaranteeing the client's background loops are started exactly once per connection.
        client.AttachToCache(() => EvictSender(key, connection, client));
        client.Linking += (_, args) => linking.Publish(args);
        client.Linked += (_, args) => RaiseLinked(args);
        client.LinkFailed += (_, args) => RaiseLinkFailed(args);
        client.Unlinked += (_, args) => RaiseUnlinked(args);
        client.PackageChanged += (_, args) => packageChanged.Publish(args);

        _ = client.Connect(new MsmtConnectOptions
        {
            Target = target,
            Credentials = options.Credentials,
            Mode = options.Mode,
            SessionLifetime = options.SessionLifetime,
            RekeyLimit = options.RekeyLimit,
            HandshakeTimeout = options.HandshakeTimeout,
            StallTimeout = options.StallTimeout,
            ResponseTimeout = options.ResponseTimeout,
            TcpKeepAliveTime = options.TcpKeepAliveTime,
            KeepAliveMinInterval = options.KeepAliveMinInterval,
            KeepAliveMaxInterval = options.KeepAliveMaxInterval,
        });

        return client;
    }

    private void EvictSender((string Host, int Port) key, MsmtConnection connection, MsmtClient client)
    {
        connection.RemoveSender(client);
        RemoveConnectionIfEmpty(key, connection);
    }

    private void RemoveConnectionIfEmpty((string Host, int Port) key, MsmtConnection connection)
    {
        // Matched by instance, not just key, so a newer connection registered under the same key is never removed.
        if (connection.TryRetire())
        {
            connections.TryRemove(KeyValuePair.Create(key, connection));
        }
    }

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
            }
        }
    }

    private async Task Sweep()
    {
        packageTracker.RemoveExpired();

        if (options.MaxIdleTime is { } maxIdleTime)
        {
            await EvictUnusedSenders(maxIdleTime);
            await EvictUnusedReceivers(maxIdleTime);
        }

        if (options.MaxConnectionCount is { } maxConnectionCount)
        {
            await EvictExcessSenders(maxConnectionCount);
            await EvictExcessReceivers(maxConnectionCount);
        }
    }

    private async Task EvictUnusedSenders(TimeSpan maxIdleTime)
    {
        foreach (KeyValuePair<(string Host, int Port), MsmtConnection> entry in connections)
        {
            if (entry.Value.SenderEngine is { IsIdle: true } sender && DateTime.UtcNow - sender.LastActivityUtc >= maxIdleTime)
            {
                await EvictIdleSender(entry.Key, maxIdleTime);
            }
        }
    }

    private async Task EvictUnusedReceivers(TimeSpan maxIdleTime)
    {
        foreach (MsmtServerConnection receiver in connections.Values.Select(connection => connection.ReceiverEngine).OfType<MsmtServerConnection>())
        {
            if (receiver.IsIdle && DateTime.UtcNow - receiver.LastActivityUtc >= maxIdleTime)
            {
                await receiver.Evict(new TimeoutException($"The connection had no application traffic for {maxIdleTime}."));
            }
        }
    }

    private async Task EvictExcessSenders(int maxConnectionCount)
    {
        List<((string Host, int Port) Key, MsmtClient Sender)> senders =
        [
            .. connections
                .Select(entry => (entry.Key, Sender: entry.Value.SenderEngine))
                .Where(entry => entry.Sender is not null)
                .Select(entry => (entry.Key, entry.Sender!)),
        ];

        int excess = senders.Count - maxConnectionCount;
        if (excess <= 0)
        {
            return;
        }

        // Never a busy connection - see MsmtClient.IsIdle.
        List<(string Host, int Port)> oldest =
        [
            .. senders
                .Where(entry => entry.Sender.IsIdle)
                .OrderBy(entry => entry.Sender.LastActivityUtc)
                .Take(excess)
                .Select(entry => entry.Key),
        ];

        foreach ((string Host, int Port) key in oldest)
        {
            await EvictIdleSender(key, TimeSpan.Zero);
        }
    }

    private async Task EvictExcessReceivers(int maxConnectionCount)
    {
        List<((string Host, int Port) Key, MsmtServerConnection Receiver)> receivers =
        [
            .. connections
                .Select(entry => (entry.Key, Receiver: entry.Value.ReceiverEngine))
                .Where(entry => entry.Receiver is not null)
                .Select(entry => (entry.Key, entry.Receiver!)),
        ];

        int excess = receivers.Count - maxConnectionCount;
        if (excess <= 0)
        {
            return;
        }

        // Never a busy connection - see MsmtServerConnection.IsIdle.
        List<MsmtServerConnection> oldest =
        [
            .. receivers
                .Where(entry => entry.Receiver.IsIdle)
                .OrderBy(entry => entry.Receiver.LastActivityUtc)
                .Take(excess)
                .Select(entry => entry.Receiver),
        ];

        foreach (MsmtServerConnection receiver in oldest)
        {
            // Re-checked, since it may have started a message cycle since being selected above.
            if (receiver.IsIdle)
            {
                await receiver.Evict(new TimeoutException($"The connection was evicted to stay within {maxConnectionCount} connections."));
            }
        }
    }

    private async Task EvictIdleSender((string Host, int Port) key, TimeSpan minIdleTime)
    {
        // RemoveIdleSender re-checks idleness under the same lock ReserveSender holds, so a send queued
        // since this sender was selected above, or about to be, keeps it attached.
        if (connections.TryGetValue(key, out MsmtConnection? connection) && connection.RemoveIdleSender(minIdleTime) is { } sender)
        {
            RemoveConnectionIfEmpty(key, connection);
            await sender.DisposeAsync();
        }
    }
}
