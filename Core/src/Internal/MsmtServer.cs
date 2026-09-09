namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// Listens for MSMT client connections, acknowledging each message once it is fully received and every
/// <see cref="Received"/> subscriber has decided whether to accept it. Used internally by <see
/// cref="MsmtPeer"/> to back its listener.
/// </summary>
internal sealed class MsmtServer : IDisposable, IAsyncDisposable
{
    private readonly CancellationTokenSource stopCancellation = new();
    private readonly List<Task> connectionTasks = [];
    private readonly Lock connectionTasksLock = new();
    private readonly ConcurrentDictionary<MsmtServerConnection, TcpClient> connections = new();

    private MsmtHostOptions options = null!;
    private TcpListener? listener;
    private Task? acceptLoop;

    /// <summary>Raised whenever a client begins a TLS handshake with this server.</summary>
    public event EventHandler<MsmtLinkingEventArgs> Linking = delegate { };

    /// <summary>Raised whenever a client completes a TLS connection to this server, once its handshake has fully completed and been secured.</summary>
    public event EventHandler<MsmtLinkedEventArgs> Linked = delegate { };

    /// <summary>Raised whenever a client's attempted TLS handshake with this server fails.</summary>
    public event EventHandler<MsmtLinkFailedEventArgs> LinkFailed = delegate { };

    /// <summary>
    /// Raised whenever this server receives an application message from a connected client. If <see
    /// cref="MsmtReceivedEventArgs.IsResponseRequested"/> is <see langword="true"/>, a subscriber may
    /// acknowledge it through <see cref="MsmtReceivedEventArgs.Responder"/>; if none does, it is accepted
    /// automatically once every subscriber has run, unless a subscriber called <see
    /// cref="IMsmtResponder.Defer"/>, in which case it stays unacknowledged - and no later message on this
    /// connection is read - until <see cref="IMsmtResponder.Accept()"/> or <see
    /// cref="IMsmtResponder.Reject()"/> is eventually called.
    /// </summary>
    public event EventHandler<MsmtReceivedEventArgs> Received = delegate { };

    /// <summary>Raised whenever a connected client's established connection closes, carrying the link that closed and, if applicable, the exception that caused it.</summary>
    public event EventHandler<MsmtUnlinkedEventArgs> Unlinked = delegate { };

    /// <summary>Never raised: an <see cref="MsmtServerConnection"/> cannot send, so no server-side send ever progresses. Exists only for parity with the events an <see cref="MsmtPeer"/> forwards.</summary>
    public event EventHandler<MsmtPackageChangedEventArgs> PackageChanged = delegate { };

    /// <summary>Gets the endpoint this server is actually listening on, once <see cref="Host"/> has been called, resolving any requested ephemeral port.</summary>
    public IPEndPoint? LocalEndPoint => (IPEndPoint?)listener?.LocalEndpoint;

    /// <summary>
    /// Binds the listening socket and starts accepting client connections in the background,
    /// transitioning this server from its initial unstarted state to a started state.
    /// </summary>
    /// <param name="options">The endpoint and credentials to listen with.</param>
    public void Host(MsmtHostOptions options)
    {
        this.options = options;
        listener = new TcpListener(MsmtProtocol.ResolveAddress(options.Host), options.Port);
        listener.Start();
        acceptLoop = Task.Run(() => AcceptLoop(stopCancellation.Token));
    }

    /// <summary>
    /// Immediately abandons this server: stops accepting new connections and signals every active
    /// connection to close, without waiting for any of them to finish. Prefer <see cref="DisposeAsync"/>
    /// for a clean, awaited shutdown; use this only when an immediate, non-blocking teardown is required.
    /// </summary>
    public void Dispose()
    {
        stopCancellation.Cancel();
        listener?.Stop();
        CloseConnectedSockets();
        stopCancellation.Dispose();
    }

    /// <summary>
    /// Cleanly shuts this server down: stops accepting new connections and waits for every active
    /// connection to finish. Prefer this over <see cref="Dispose"/> whenever an awaited shutdown is
    /// acceptable.
    /// </summary>
    /// <returns>A task that completes once every connection has finished.</returns>
    public async ValueTask DisposeAsync()
    {
        await stopCancellation.CancelAsync();
        listener?.Stop();

        // A connection idle at shutdown is typically blocked in a synchronous read the BouncyCastle TLS
        // stream doesn't actually abort for a cancelled token (it falls back to a byte[]-based ReadAsync
        // overload it never overrides), so cancellation alone would never unblock HandleConnection and this
        // method would hang forever awaiting it below. Closing the socket directly does forcibly interrupt
        // that blocked read.
        CloseConnectedSockets();

        if (acceptLoop is not null)
        {
            await acceptLoop;
        }

        Task[] pending;
        lock (connectionTasksLock)
        {
            pending = [.. connectionTasks];
        }

        await Task.WhenAll(pending);
        stopCancellation.Dispose();
    }

    private void CloseConnectedSockets()
    {
        foreach (TcpClient client in connections.Values)
        {
            CloseSocket(client);
        }
    }

    private void CloseSocket(TcpClient client)
    {
        try
        {
            client.Close();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }

    private async Task AcceptLoop(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener!.AcceptTcpClientAsync(cancellation);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            Task connectionTask = HandleConnection(client, cancellation);
            lock (connectionTasksLock)
            {
                connectionTasks.Add(connectionTask);
            }

            _ = connectionTask.ContinueWith(RemoveConnectionTask, TaskScheduler.Default);
        }
    }

    private void RemoveConnectionTask(Task connectionTask)
    {
        lock (connectionTasksLock)
        {
            connectionTasks.Remove(connectionTask);
        }
    }

    private async Task HandleConnection(TcpClient client, CancellationToken cancellation)
    {
        using TcpClient tcpClient = client;
        TlsServerProtocol protocol = new(tcpClient.GetStream());
        MsmtTlsServer tlsServer = new(options);

        IPEndPoint remoteEndPoint = (IPEndPoint)tcpClient.Client.RemoteEndPoint!;
        MsmtTarget target = new() { Host = remoteEndPoint.Address.ToString(), Port = remoteEndPoint.Port };
        MsmtServerConnection connection = new(target);
        TaskCompletionSource disconnectedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.AttachCloser(() => CloseSocket(tcpClient));
        connection.AttachDisconnector(async () =>
        {
            CloseSocket(tcpClient);
            await disconnectedSource.Task;
        });
        Linking.Invoke(this, new MsmtLinkingEventArgs { Link = connection });

        try
        {
            protocol.Accept(tlsServer);
        }
        catch (Exception exception)
        {
            LinkFailed.Invoke(this, new MsmtLinkFailedEventArgs { Link = connection, Exception = exception });
            disconnectedSource.TrySetResult();
            return;
        }

        connection.SetIdentity(tlsServer.ClientIdentity!);
        connection.MarkConnected();
        Exception? disconnectionException = null;
        connections[connection] = tcpClient;

        try
        {
            Linked.Invoke(this, new MsmtLinkedEventArgs { Link = connection });

            Stream stream = protocol.Stream;
            DateTime? sessionExpiresAtUtc = null;
            bool isFirstMessage = true;
            byte[] headerBuffer = new byte[MsmtHeader.Size];

            while (!cancellation.IsCancellationRequested)
            {
                connection.MarkIdle();

                CancellationTokenSource? idleTimeoutCancellation = null;
                Task? idleTimeoutTask = null;
                int[]? timedOut = null;
                if (options.MaxIdleTime is { } maxIdleTime)
                {
                    idleTimeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    timedOut = new int[1];
                    idleTimeoutTask = RunIdleTimeout(maxIdleTime, tcpClient, idleTimeoutCancellation.Token, timedOut);
                }

                try
                {
                    try
                    {
                        await MsmtProtocol.ReadExact(stream, headerBuffer, cancellation);
                    }
                    catch (IOException exception)
                    {
                        disconnectionException = timedOut is not null && Volatile.Read(ref timedOut[0]) == 1
                            ? new TimeoutException($"The connection was idle for longer than {options.MaxIdleTime}.")
                            : exception;
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
                finally
                {
                    if (idleTimeoutCancellation is not null)
                    {
                        await idleTimeoutCancellation.CancelAsync();
                        idleTimeoutCancellation.Dispose();
                        await idleTimeoutTask!;
                    }
                }

                connection.MarkBusy();

                MsmtHeader requestHeader = MsmtHeader.Read(headerBuffer);

                if (!requestHeader.IsWellFormed())
                {
                    await Respond(stream, requestHeader, MsmtMessageFlags.InvalidPreambleOrModeUnsupported, ReadOnlyMemory<byte>.Empty, cancellation);
                    return;
                }

                IMemoryOwner<byte> payload = await MsmtProtocol.ReadPooled(stream, (int)requestHeader.Length, cancellation);

                if ((requestHeader.Flags & MsmtMessageFlags.SessionModeNegotiation) == MsmtMessageFlags.SessionModeNegotiation)
                {
                    bool shouldClose;
                    try
                    {
                        (sessionExpiresAtUtc, shouldClose) = await RespondToSessionNegotiation(stream, requestHeader, payload.Memory, isFirstMessage, cancellation);
                    }
                    finally
                    {
                        payload.Dispose();
                    }

                    isFirstMessage = false;
                    if (shouldClose)
                    {
                        return;
                    }

                    continue;
                }

                isFirstMessage = false;

                if ((requestHeader.Flags & MsmtMessageFlags.ReachabilityCheck) == MsmtMessageFlags.ReachabilityCheck)
                {
                    try
                    {
                        await Respond(stream, requestHeader, MsmtMessageFlags.ReachabilityCheck, payload.Memory, cancellation);
                    }
                    finally
                    {
                        payload.Dispose();
                    }
                }
                else
                {
                    // Ownership of payload transfers into RespondToMessage's Received event; it is not disposed here.
                    await RespondToMessage(connection, stream, requestHeader, payload, cancellation);
                }

                // Only a negotiated Session Mode lifetime forces a close; Message/MessageWithRekeying rely on the client to close instead.
                if (sessionExpiresAtUtc is not null && DateTime.UtcNow >= sessionExpiresAtUtc)
                {
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            disconnectionException = exception;
            throw;
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

            connection.MarkDisconnected();
            connections.TryRemove(connection, out _);
            Unlinked.Invoke(this, new MsmtUnlinkedEventArgs { Link = connection, Exception = disconnectionException });
            disconnectedSource.TrySetResult();
        }
    }

    /// <summary>
    /// Closes <paramref name="tcpClient"/>'s socket once <paramref name="duration"/> has genuinely elapsed,
    /// setting <paramref name="timedOut"/>'s single element to <c>1</c> first so the caller can distinguish
    /// this from any other cause of the resulting read failure. Waits in a chain of clamped-length timers
    /// rather than a single one (see <see cref="MsmtProtocol.ClampToMaxTimerDuration"/>) so an arbitrarily
    /// long <paramref name="duration"/> is honored exactly rather than throwing or firing early. BouncyCastle's
    /// TLS stream falls back to a blocking byte[]-based ReadAsync overload it never overrides internally
    /// (see <see cref="DisposeAsync"/>), so a cancelled token alone cannot interrupt the caller's
    /// in-progress read - closing the socket directly, exactly like shutdown does, is the only way to force
    /// it to unblock.
    /// </summary>
    /// <param name="duration">How long the connection may stay idle before its socket is closed.</param>
    /// <param name="tcpClient">The socket to close once idle for <paramref name="duration"/>.</param>
    /// <param name="cancellation">Stops waiting without closing the socket, e.g. once a message arrives.</param>
    /// <param name="timedOut">A single-element flag this method sets before closing the socket.</param>
    private async Task RunIdleTimeout(TimeSpan duration, TcpClient tcpClient, CancellationToken cancellation, int[] timedOut)
    {
        // Tracked as a remaining TimeSpan, decremented by however long each wait actually clamped to,
        // rather than an absolute DateTime deadline - duration as large as TimeSpan.MaxValue would overflow
        // DateTime.UtcNow + duration well before it could ever overflow a TimeSpan subtraction.
        TimeSpan remaining = duration;

        while (!cancellation.IsCancellationRequested)
        {
            if (remaining <= TimeSpan.Zero)
            {
                Volatile.Write(ref timedOut[0], 1);
                CloseSocket(tcpClient);
                return;
            }

            TimeSpan wait = MsmtProtocol.ClampToMaxTimerDuration(remaining);

            try
            {
                await Task.Delay(wait, cancellation);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            remaining -= wait;
        }
    }

    private async Task Respond(Stream stream, MsmtHeader requestHeader, MsmtMessageFlags flags, ReadOnlyMemory<byte> payload, CancellationToken cancellation)
    {
        MsmtHeader responseHeader = new()
        {
            Version = MsmtHeader.SupportedVersion,
            Flags = flags,
            MessageId = requestHeader.MessageId,
            Length = (uint)payload.Length,
        };

        byte[] headerBuffer = new byte[MsmtHeader.Size];
        responseHeader.Write(headerBuffer);
        await stream.WriteAsync(headerBuffer, cancellation);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, cancellation);
        }
    }

    private async Task RespondToMessage(MsmtServerConnection connection, Stream stream, MsmtHeader requestHeader, IMemoryOwner<byte> payload, CancellationToken cancellation)
    {
        bool isResponseRequested = (requestHeader.Flags & MsmtMessageFlags.AcknowledgementRequestedOrGiven) == MsmtMessageFlags.AcknowledgementRequestedOrGiven;
        MsmtResponder responder = new(isResponseRequested);

        try
        {
            Received.Invoke(this, new MsmtReceivedEventArgs { Link = connection, Payload = payload, Responder = responder, IsResponseRequested = isResponseRequested });
        }
        finally
        {
            payload.Dispose();
        }

        if (isResponseRequested && responder.Response == MsmtResponseKind.None)
        {
            // A deferred responder is decided by something other than the subscriber that raised Received
            // - possibly well after it has already returned - so this message is only acknowledged, and no
            // further message on this connection read, once that eventually happens.
            if (responder.IsDeferred)
            {
                await responder.WaitForDecision(cancellation);
            }
            else
            {
                responder.Accept();
            }
        }

        // None only ever occurs here for a Send that never requested a response at all (isResponseRequested
        // guarantees Accept/Reject above), which this implicitly reports as success since there was no ack
        // request through which the application could have rejected it.
        MsmtMessageFlags responseFlags = responder.Response == MsmtResponseKind.Reject ? MsmtMessageFlags.None : MsmtMessageFlags.MessageSuccess;
        if (isResponseRequested)
        {
            responseFlags |= MsmtMessageFlags.AcknowledgementRequestedOrGiven;
        }

        using IMemoryOwner<byte> responsePayload = responder.Payload;
        await Respond(stream, requestHeader, responseFlags, responsePayload.Memory, cancellation);
    }

    private async Task<(DateTime? ExpiresAtUtc, bool ShouldClose)> RespondToSessionNegotiation(Stream stream, MsmtHeader requestHeader, ReadOnlyMemory<byte> payload, bool isFirstMessage, CancellationToken cancellation)
    {
        if (!options.SupportsSessionMode)
        {
            await Respond(stream, requestHeader, MsmtMessageFlags.SessionModeUnsupported, ReadOnlyMemory<byte>.Empty, cancellation);
            return (null, true);
        }

        // Per the ICD, Session Mode Negotiation is only honored as the very first message on the connection.
        if (!isFirstMessage)
        {
            await Respond(stream, requestHeader, MsmtMessageFlags.SessionModeRejected, ReadOnlyMemory<byte>.Empty, cancellation);
            return (null, false);
        }

        int requestedSeconds;
        try
        {
            requestedSeconds = int.Parse(Encoding.ASCII.GetString(payload.Span), CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            requestedSeconds = 0;
        }

        if (requestedSeconds <= 0)
        {
            await Respond(stream, requestHeader, MsmtMessageFlags.SessionModeUnsupported, ReadOnlyMemory<byte>.Empty, cancellation);
            return (null, true);
        }

        int agreedSeconds = Math.Min(requestedSeconds, (int)options.MaximumSessionLifetime.TotalSeconds);
        byte[] responsePayload = Encoding.ASCII.GetBytes(agreedSeconds.ToString(CultureInfo.InvariantCulture));

        await Respond(stream, requestHeader, MsmtMessageFlags.SessionModeAccepted, responsePayload, cancellation);
        return (DateTime.UtcNow.AddSeconds(agreedSeconds), false);
    }
}
