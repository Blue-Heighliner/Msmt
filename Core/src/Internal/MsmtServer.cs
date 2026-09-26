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
    private readonly TimeSpan acceptRetryDelay = TimeSpan.FromMilliseconds(100);
    private readonly TimeSpan sessionExpiryGrace = TimeSpan.FromSeconds(2);

    private MsmtHostOptions options = null!;
    private TcpListener? listener;
    private Task? acceptLoop;
    private int disposed;

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
        CancellationToken cancellation = stopCancellation.Token;
        acceptLoop = Task.Run(() => AcceptLoop(cancellation));
    }

    /// <summary>
    /// Immediately abandons this server: stops accepting new connections and signals every active
    /// connection to close, without waiting for any of them to finish. Prefer <see cref="DisposeAsync"/>
    /// for a clean, awaited shutdown; use this only when an immediate, non-blocking teardown is required.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

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
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await stopCancellation.CancelAsync();
        listener?.Stop();

        // A connection at shutdown is typically blocked in BouncyCastle's synchronous handshake or in a read
        // its TLS stream doesn't abort for a cancelled token (it falls back to a byte[]-based ReadAsync
        // overload it never overrides), so cancellation alone would never unblock HandleConnection and this
        // method would hang forever awaiting it below. Closing the socket directly forcibly interrupts both.
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
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException || cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                // One client's failed accept (e.g. reset before it was accepted) must not stop the listener for
                // every other client; the delay keeps a persistent failure (e.g. out of file descriptors) from spinning.
                try
                {
                    await Task.Delay(acceptRetryDelay, cancellation);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            // Run on its own task since BouncyCastle's handshake blocks synchronously; one client stalling
            // mid-handshake must never delay accepting the next.
            Task connectionTask = Task.Run(() => HandleConnection(client, cancellation), CancellationToken.None);
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
        using MsmtWatchdog watchdog = new(() => CloseSocket(tcpClient));
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

        // Registered before the handshake so shutdown can abort one that stalls, then re-checked in case
        // shutdown's own sweep of connections ran just before this registration.
        connections[connection] = tcpClient;
        if (cancellation.IsCancellationRequested)
        {
            CloseSocket(tcpClient);
        }

        try
        {
            TlsServerProtocol protocol;
            try
            {
                Linking.Invoke(this, new MsmtLinkingEventArgs { Link = connection });
                MsmtProtocol.ApplyTcpKeepAlive(tcpClient.Client, options.TcpKeepAliveTime);
                protocol = new(new MsmtWatchedStream(tcpClient.GetStream(), watchdog));

                using (watchdog.Guard(options.HandshakeTimeout, false))
                {
                    protocol.Accept(tlsServer);
                }
            }
            catch (Exception exception)
            {
                Exception failure = watchdog.HasExpired ? new TimeoutException("The client did not complete its handshake within the configured timeout.", exception) : exception;
                LinkFailed.Invoke(this, new MsmtLinkFailedEventArgs { Link = connection, Exception = failure });
                return;
            }

            connection.SetIdentity(tlsServer.ClientIdentity!);
            connection.MarkConnected();
            Exception? disconnectionException = null;

            try
            {
                Linked.Invoke(this, new MsmtLinkedEventArgs { Link = connection });
                disconnectionException = await ServeMessages(connection, watchdog, protocol.Stream, cancellation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                disconnectionException = DescribeDisconnection(connection, watchdog, exception);
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

                connection.MarkDisconnected();
                Unlinked.Invoke(this, new MsmtUnlinkedEventArgs { Link = connection, Exception = disconnectionException });
            }
        }
        finally
        {
            connections.TryRemove(connection, out _);
            disconnectedSource.TrySetResult();
        }
    }

    private Exception? DescribeDisconnection(MsmtServerConnection connection, MsmtWatchdog watchdog, Exception exception)
    {
        if (connection.HasCloseReason)
        {
            return connection.CloseReason;
        }

        return watchdog.HasExpired ? new TimeoutException("The client stalled and the connection was dropped.", exception) : exception;
    }

    /// <summary>Reads and acknowledges messages on an established connection, one at a time, until it closes.</summary>
    /// <param name="connection">The connection being served.</param>
    /// <param name="watchdog">Bounds the phases in which the client owes this server something.</param>
    /// <param name="stream">The connection's secured TLS stream.</param>
    /// <param name="cancellation">Signals server shutdown.</param>
    /// <returns>The exception that closed the connection, or <see langword="null"/> if it closed normally.</returns>
    private async Task<Exception?> ServeMessages(MsmtServerConnection connection, MsmtWatchdog watchdog, Stream stream, CancellationToken cancellation)
    {
        DateTime? sessionExpiresAtUtc = null;
        bool isFirstMessage = true;
        int messageCount = 0;
        byte[] headerBuffer = new byte[MsmtHeader.Size];
        using CancellationTokenSource lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task lifetimeTask = Task.CompletedTask;

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                connection.MarkIdle();

                // A compliant client only connects to send, so silence right after the handshake is a stall.
                // Later gaps are legitimate idleness, which the peer's pool eviction and the session
                // lifetime handle instead.
                using (watchdog.Guard(isFirstMessage ? options.HandshakeTimeout : null, false))
                {
                    await MsmtProtocol.ReadExact(stream, headerBuffer.AsMemory(0, 1), cancellation);
                }

                connection.MarkBusy();

                MsmtHeader requestHeader;
                IMemoryOwner<byte>? payload = null;
                using (watchdog.Guard(options.StallTimeout, true))
                {
                    await MsmtProtocol.ReadExact(stream, headerBuffer.AsMemory(1), cancellation);
                    requestHeader = MsmtHeader.Read(headerBuffer);

                    if (requestHeader.IsWellFormed())
                    {
                        payload = await MsmtProtocol.ReadPooled(stream, (int)requestHeader.Length, cancellation);
                    }
                }

                if (payload is null)
                {
                    await Respond(stream, watchdog, requestHeader, MsmtMessageFlags.InvalidPreambleOrModeUnsupported, ReadOnlyMemory<byte>.Empty, cancellation);
                    return null;
                }

                if ((requestHeader.Flags & MsmtMessageFlags.SessionModeNegotiation) == MsmtMessageFlags.SessionModeNegotiation)
                {
                    DateTime? agreedExpiresAtUtc;
                    bool shouldClose;
                    try
                    {
                        (agreedExpiresAtUtc, shouldClose) = await RespondToSessionNegotiation(stream, watchdog, requestHeader, payload.Memory, isFirstMessage, cancellation);
                    }
                    finally
                    {
                        payload.Dispose();
                    }

                    isFirstMessage = false;
                    if (shouldClose)
                    {
                        return null;
                    }

                    if (agreedExpiresAtUtc is { } expiresAtUtc)
                    {
                        sessionExpiresAtUtc = expiresAtUtc;
                        lifetimeTask = CloseAtSessionExpiry(expiresAtUtc - DateTime.UtcNow + sessionExpiryGrace, connection, lifetimeCancellation.Token);
                    }

                    continue;
                }

                isFirstMessage = false;

                if ((requestHeader.Flags & MsmtMessageFlags.ReachabilityCheck) == MsmtMessageFlags.ReachabilityCheck)
                {
                    try
                    {
                        await Respond(stream, watchdog, requestHeader, MsmtMessageFlags.ReachabilityCheck, payload.Memory, cancellation);
                    }
                    finally
                    {
                        payload.Dispose();
                    }
                }
                else
                {
                    // RespondToMessage disposes payload itself, once every Received subscriber has run.
                    await RespondToMessage(connection, stream, watchdog, requestHeader, payload, cancellation);
                    connection.MarkActivity();
                }

                // Only a negotiated Session Mode lifetime forces a close on its own; otherwise the connection
                // serves as many messages as the rekey limit allows, and then closes just as its client does.
                if (sessionExpiresAtUtc is { } expiry)
                {
                    if (DateTime.UtcNow >= expiry)
                    {
                        return null;
                    }
                }
                else if (++messageCount >= options.RekeyLimit)
                {
                    return null;
                }
            }

            return null;
        }
        finally
        {
            await lifetimeCancellation.CancelAsync();
            await lifetimeTask;
        }
    }

    /// <summary>
    /// Closes an idle connection once its negotiated session lifetime, plus a grace period that lets the
    /// client close first, has elapsed - a backstop for a client that goes silent, since the lifetime is
    /// otherwise only checked after a message. Waits in a chain of clamped-length timers rather than a
    /// single one (see <see cref="MsmtProtocol.ClampToMaxTimerDuration"/>) so an arbitrarily long lifetime
    /// is honored exactly. A connection mid-message is left to the check that follows the message.
    /// </summary>
    /// <param name="duration">How long from now the connection may stay open.</param>
    /// <param name="connection">The connection to close.</param>
    /// <param name="cancellation">Stops waiting without closing anything, once the connection ends.</param>
    private async Task CloseAtSessionExpiry(TimeSpan duration, MsmtServerConnection connection, CancellationToken cancellation)
    {
        TimeSpan remaining = duration;

        while (remaining > TimeSpan.Zero)
        {
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

        if (connection.IsIdle)
        {
            connection.CloseWith(null);
        }
    }

    private async Task Respond(Stream stream, MsmtWatchdog watchdog, MsmtHeader requestHeader, MsmtMessageFlags flags, ReadOnlyMemory<byte> payload, CancellationToken cancellation)
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

        using (watchdog.Guard(options.StallTimeout, true))
        {
            await stream.WriteAsync(headerBuffer, cancellation);
            if (payload.Length > 0)
            {
                await stream.WriteAsync(payload, cancellation);
            }
        }
    }

    private async Task RespondToMessage(MsmtServerConnection connection, Stream stream, MsmtWatchdog watchdog, MsmtHeader requestHeader, IMemoryOwner<byte> payload, CancellationToken cancellation)
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
        await Respond(stream, watchdog, requestHeader, responseFlags, responsePayload.Memory, cancellation);
    }

    private async Task<(DateTime? ExpiresAtUtc, bool ShouldClose)> RespondToSessionNegotiation(Stream stream, MsmtWatchdog watchdog, MsmtHeader requestHeader, ReadOnlyMemory<byte> payload, bool isFirstMessage, CancellationToken cancellation)
    {
        if (!options.SupportsSessionMode)
        {
            await Respond(stream, watchdog, requestHeader, MsmtMessageFlags.SessionModeUnsupported, ReadOnlyMemory<byte>.Empty, cancellation);
            return (null, true);
        }

        // Per the ICD, Session Mode Negotiation is only honored as the very first message on the connection.
        if (!isFirstMessage)
        {
            await Respond(stream, watchdog, requestHeader, MsmtMessageFlags.SessionModeRejected, ReadOnlyMemory<byte>.Empty, cancellation);
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
            await Respond(stream, watchdog, requestHeader, MsmtMessageFlags.SessionModeUnsupported, ReadOnlyMemory<byte>.Empty, cancellation);
            return (null, true);
        }

        int agreedSeconds = Math.Min(requestedSeconds, (int)options.MaximumSessionLifetime.TotalSeconds);
        byte[] responsePayload = Encoding.ASCII.GetBytes(agreedSeconds.ToString(CultureInfo.InvariantCulture));

        await Respond(stream, watchdog, requestHeader, MsmtMessageFlags.SessionModeAccepted, responsePayload, cancellation);
        return (DateTime.UtcNow.AddSeconds(agreedSeconds), false);
    }
}
