namespace BlueHeighliner.Msmt.Internal;

/// <inheritdoc cref="IMsmtPeer" />
/// <remarks>
/// Idle timeout and connection count limits (see <see cref="MsmtOptions.MaxIdleTime"/> and <see
/// cref="MsmtOptions.MaxConnectionCount"/>) apply symmetrically to both the outgoing links this peer
/// creates on demand in <see cref="Send"/> and the incoming links its internal <see cref="MsmtServer"/>
/// receiver accepts, counted independently per direction, and never interrupt a link with a
/// send/message cycle currently in progress.
/// </remarks>
internal sealed class MsmtPeer : IMsmtPeer
{
    private readonly MsmtOptions options;
    private readonly ConcurrentDictionary<(string Host, int Port), MsmtConnection> connections = new();
    private readonly ConcurrentDictionary<IMsmtConnection, byte> activeConnections = new();
    private readonly CancellationTokenSource disposalCancellation = new();
    private readonly TimeSpan evictionCheckInterval = TimeSpan.FromSeconds(1);
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

    /// <summary>
    /// Creates a peer, immediately starting its background on-demand connection eviction loop. Call <see
    /// cref="StartListener"/> separately to also start listening.
    /// </summary>
    /// <param name="options">Shared credentials and connection-behavior defaults for this peer.</param>
    public MsmtPeer(MsmtOptions options)
    {
        this.options = options;
        evictionLoop = Task.Run(EvictionLoop);
    }

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
    public IReadOnlyList<IMsmtPackage> Packages =>
        [.. connections.Values.Where(connection => connection.SenderEngine is not null).SelectMany(connection => connection.SenderEngine!.Packages)];

    /// <inheritdoc />
    public void StartListener(int port = 0, string host = "0.0.0.0")
    {
        server?.Dispose();

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
        newServer.Host(new MsmtHostOptions
        {
            Host = host,
            Port = port,
            Credentials = options.Credentials,
            SupportsSessionMode = options.SupportsSessionMode,
            MaximumSessionLifetime = options.MaximumSessionLifetime,
            RequireFullyQualifiedHostname = options.RequireFullyQualifiedHostname,
            MaxIdleTime = options.MaxIdleTime,
        });
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
        // Validated before GetOrCreateConnection so an oversized payload never opens a connection it can't use.
        MsmtProtocol.ValidatePayloadLength(payload.Memory.Length, nameof(payload));
        MsmtConnection connection = GetOrCreateConnection(target);
        connection.SenderEngine!.Send(payload, options);
    }

    /// <inheritdoc />
    public async Task<MsmtResponse> Request(MsmtNameTarget target, IMemoryOwner<byte> payload, MsmtSendOptions? options = null, CancellationToken cancellation = default)
    {
        MsmtProtocol.ValidatePayloadLength(payload.Memory.Length, nameof(payload));
        MsmtConnection connection = GetOrCreateConnection(target);
        return await connection.SenderEngine!.Request(payload, options, cancellation);
    }

    /// <inheritdoc />
    public IMsmtPackage? GetPackage(object tag) =>
        connections.Values
            .Where(connection => connection.SenderEngine is not null)
            .Select(connection => connection.SenderEngine!.GetPackage(tag))
            .FirstOrDefault(package => package is not null);

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
        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(target.Host, target.Port, cancellation);

        MsmtConnectOptions connectOptions = new()
        {
            Target = target,
            Credentials = options.Credentials,
        };

        TlsClientProtocol protocol = new(tcpClient.GetStream());
        protocol.Connect(new MsmtTlsClient(connectOptions));

        try
        {
            Stream stream = protocol.Stream;

            MsmtHeader requestHeader = new()
            {
                Version = MsmtHeader.SupportedVersion,
                Flags = MsmtMessageFlags.ReachabilityCheck,
                MessageId = MsmtProtocol.GenerateMessageId(),
                Length = 0,
            };

            byte[] headerBuffer = new byte[MsmtHeader.Size];
            requestHeader.Write(headerBuffer);
            await stream.WriteAsync(headerBuffer, cancellation);

            await MsmtProtocol.ReadExact(stream, headerBuffer, cancellation);
            MsmtHeader responseHeader = MsmtHeader.Read(headerBuffer);

            if (!responseHeader.IsWellFormed())
            {
                return false;
            }

            if (responseHeader.Length > 0)
            {
                await MsmtProtocol.ReadExact(stream, new byte[responseHeader.Length], cancellation);
            }

            return responseHeader.Acknowledges(requestHeader) && (responseHeader.Flags & MsmtMessageFlags.ReachabilityCheck) == MsmtMessageFlags.ReachabilityCheck;
        }
        finally
        {
            try
            {
                protocol.Close();
            }
            catch (IOException)
            {
            }
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
        disposalCancellation.Cancel();
        server?.Dispose();

        foreach (MsmtConnection connection in connections.Values)
        {
            connection.SenderEngine?.Dispose();
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
        foreach (MsmtConnection connection in connections.Values)
        {
            if (connection.SenderEngine is not null)
            {
                await connection.SenderEngine.DisposeAsync();
            }
        }

        if (server is not null)
        {
            await server.DisposeAsync();
        }

        disposalCancellation.Dispose();
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
        MsmtConnection connection = connections.GetOrAdd((link.Target.Host, link.Target.Port), _ => new MsmtConnection(link.Target));
        connection.AttachReceiver(link);
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

    private MsmtConnection GetOrCreateConnection(MsmtNameTarget target)
    {
        MsmtTarget plainTarget = new() { Host = target.Host, Port = target.Port };
        MsmtConnection connection = connections.GetOrAdd((target.Host, target.Port), _ => new MsmtConnection(plainTarget));
        connection.EnsureSender(() => CreateSender(connection, (target.Host, target.Port), target));
        return connection;
    }

    private MsmtClient CreateSender(MsmtConnection connection, (string Host, int Port) key, MsmtNameTarget target)
    {
        MsmtClient client = new();

        // Built and only started below: ConcurrentDictionary offers no way to run a side-effecting factory
        // exactly once, so this factory only ever runs under MsmtConnection.EnsureSender's own lock,
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
            MaxIdleTime = options.MaxIdleTime,
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
        if (connection.IsEmpty)
        {
            connections.TryRemove(key, out _);
        }
    }

    private async Task EvictionLoop()
    {
        using PeriodicTimer timer = new(evictionCheckInterval);
        while (await timer.WaitForNextTickAsync(disposalCancellation.Token))
        {
            // MaxIdleTime is enforced per-connection instead (see MsmtClient/MsmtServer), so this loop is
            // only responsible for MaxConnectionCount, counted separately for each direction.
            if (options.MaxConnectionCount is { } maxConnectionCount)
            {
                await EvictExcessSenders(maxConnectionCount);
                await EvictExcessReceivers(maxConnectionCount);
            }
        }
    }

    private async Task EvictExcessSenders(int maxConnectionCount)
    {
        List<KeyValuePair<(string Host, int Port), MsmtConnection>> withSenders =
            [.. connections.Where(entry => entry.Value.SenderEngine is not null)];

        int excess = withSenders.Count - maxConnectionCount;
        if (excess <= 0)
        {
            return;
        }

        // Never a busy connection - see MsmtClient.IsIdle.
        List<(string Host, int Port)> oldest =
        [
            .. withSenders
                .Where(entry => entry.Value.SenderEngine!.IsIdle)
                .OrderBy(entry => entry.Value.SenderEngine!.LastActivityUtc)
                .Take(excess)
                .Select(entry => entry.Key),
        ];

        foreach ((string Host, int Port) key in oldest)
        {
            await EvictConnection(key);
        }
    }

    private async Task EvictExcessReceivers(int maxConnectionCount)
    {
        List<KeyValuePair<(string Host, int Port), MsmtConnection>> withReceivers =
            [.. connections.Where(entry => entry.Value.ReceiverEngine is not null)];

        int excess = withReceivers.Count - maxConnectionCount;
        if (excess <= 0)
        {
            return;
        }

        // Never a busy connection - see MsmtServerConnection.IsIdle.
        List<(string Host, int Port)> oldest =
        [
            .. withReceivers
                .Where(entry => entry.Value.ReceiverEngine!.IsIdle)
                .OrderBy(entry => entry.Value.ReceiverEngine!.LastActivityUtc)
                .Take(excess)
                .Select(entry => entry.Key),
        ];

        foreach ((string Host, int Port) key in oldest)
        {
            await EvictReceiver(key);
        }
    }

    private async Task EvictConnection((string Host, int Port) key)
    {
        // Re-checks IsIdle here, since it may have started a new send since being selected as a candidate above.
        if (connections.TryGetValue(key, out MsmtConnection? connection) && connection.SenderEngine is { IsIdle: true } client)
        {
            connection.RemoveSender(client);
            RemoveConnectionIfEmpty(key, connection);
            await client.DisposeAsync();
        }
    }

    private async Task EvictReceiver((string Host, int Port) key)
    {
        // Re-checks IsIdle here, since it may have started a message cycle since being selected as a candidate above.
        if (connections.TryGetValue(key, out MsmtConnection? connection) && connection.ReceiverEngine is { IsIdle: true } receiver)
        {
            await receiver.Disconnect();
        }
    }
}
