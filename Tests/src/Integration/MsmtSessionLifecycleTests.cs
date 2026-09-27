namespace BlueHeighliner.Msmt.Tests.Integration;

/// <summary>Integration tests for how session connections time out, are kept alive, are bounded in lifetime, and fail - an <see cref="MsmtSessionPeer"/> never disconnects one automatically for being idle or unused.</summary>
public sealed class MsmtSessionLifecycleTests
{
    private readonly TimeSpan waitLimit = TimeSpan.FromSeconds(10);

    /// <summary>A client that connects but never starts a TLS handshake is dropped after the handshake timeout, reported as a timed out connection failure.</summary>
    [Fact]
    public async Task Listener_ClientNeverHandshakes_ConnectFailsWithTimeout()
    {
        (_, X509Certificate2 listeningCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtSessionPeer listening = new(Options(listeningCertificate, trustedAuthorities) with { HandshakeTimeout = TimeSpan.FromMilliseconds(300) });
        Task<MsmtDisconnection> failed = WaitFor(listening.Disconnected);
        listening.StartListener(0, "127.0.0.1");

        using TcpClient silent = new();
        await silent.ConnectAsync(IPAddress.Loopback, listening.Listener!.Port);

        Assert.IsType<TimeoutException>((await failed).Exception);
    }

    /// <summary>A client that completes its handshake but never negotiates a session is dropped after the handshake timeout.</summary>
    [Fact]
    public async Task Listener_ClientNeverNegotiates_ConnectFailsWithTimeout()
    {
        (X509Certificate2 connectingCertificate, X509Certificate2 listeningCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtSessionPeer listening = new(Options(listeningCertificate, trustedAuthorities) with { HandshakeTimeout = TimeSpan.FromMilliseconds(1500) });
        Task<MsmtDisconnection> failed = WaitFor(listening.Disconnected);
        listening.StartListener(0, "127.0.0.1");

        using MsmtTlsChannel raw = await ConnectRaw(listening.Listener!, connectingCertificate, trustedAuthorities);

        Assert.IsType<TimeoutException>((await failed).Exception);
    }

    /// <summary>A client that sends an ordinary message instead of negotiating a session is told the mode is unsupported and turned away.</summary>
    [Fact]
    public async Task Listener_ClientSkipsNegotiation_IsRejectedWithModeUnsupported()
    {
        (X509Certificate2 connectingCertificate, X509Certificate2 listeningCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtSessionPeer listening = new(Options(listeningCertificate, trustedAuthorities));
        Task<MsmtDisconnection> failed = WaitFor(listening.Disconnected);
        listening.StartListener(0, "127.0.0.1");

        using MsmtTlsChannel raw = await ConnectRaw(listening.Listener!, connectingCertificate, trustedAuthorities);
        await WriteFrame(raw, new MsmtHeader { Version = MsmtHeader.SupportedVersion, Flags = MsmtMessageFlags.None, MessageId = 2, Length = 0 }, []);

        (MsmtHeader response, _) = await ReadFrame(raw);
        Assert.Equal(MsmtMessageFlags.InvalidPreambleOrModeUnsupported, response.Flags);
        Assert.IsType<NotSupportedException>((await failed).Exception);
    }

    /// <summary>A listener answers a malformed header with the invalid preamble flag and drops the connection.</summary>
    [Fact]
    public async Task Listener_MalformedHeader_IsAnsweredWithInvalidPreambleAndDropped()
    {
        (X509Certificate2 connectingCertificate, X509Certificate2 listeningCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtSessionPeer listening = new(Options(listeningCertificate, trustedAuthorities));
        Task<MsmtDisconnection> disconnected = WaitFor(listening.Disconnected);
        listening.StartListener(0, "127.0.0.1");

        using MsmtTlsChannel raw = await ConnectRawSession(listening.Listener!, connectingCertificate, trustedAuthorities, "60");
        await WriteFrame(raw, new MsmtHeader { Version = 99, Flags = MsmtMessageFlags.None, MessageId = 4, Length = 0 }, []);

        (MsmtHeader response, _) = await ReadFrame(raw);
        Assert.Equal(MsmtMessageFlags.InvalidPreambleOrModeUnsupported, response.Flags);
        Assert.NotNull((await disconnected).Exception);
    }

    /// <summary>A client that stalls part way through a message header or payload is dropped after the stall timeout.</summary>
    [Theory]
    [InlineData(5)]
    [InlineData(20)]
    public async Task Listener_ClientStallsMidMessage_DisconnectsWithTimeout(int bytesSent)
    {
        (X509Certificate2 connectingCertificate, X509Certificate2 listeningCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtSessionPeer listening = new(Options(listeningCertificate, trustedAuthorities) with { StallTimeout = TimeSpan.FromMilliseconds(300) });
        Task<MsmtDisconnection> disconnected = WaitFor(listening.Disconnected);
        listening.StartListener(0, "127.0.0.1");

        using MsmtTlsChannel raw = await ConnectRawSession(listening.Listener!, connectingCertificate, trustedAuthorities, "60");
        byte[] message = new byte[MsmtHeader.Size + 100];
        new MsmtHeader { Version = MsmtHeader.SupportedVersion, Flags = MsmtMessageFlags.None, MessageId = 2, Length = 100 }.Write(message);
        await raw.Write(message.AsMemory(0, bytesSent), CancellationToken.None);

        Assert.IsType<TimeoutException>((await disconnected).Exception);
    }

    /// <summary>A message the remote application never acknowledges fails with a timeout once the response timeout passes, and closes the connection.</summary>
    [Fact]
    public async Task Request_NeverAcknowledged_FailsWithTimeoutAndClosesConnection()
    {
        (IMsmtSessionPeer listening, IMsmtSessionPeer connecting, IMsmtConnection connectingSide, IMsmtConnection listeningSide, _, _) = await ConnectPair(connecting: options => options with { ResponseTimeout = TimeSpan.FromMilliseconds(500) });
        await using (listening)
        await using (connecting)
        {
            listening.Receiver = async (connection, payload, isResponseRequested) =>
            {
                await Task.Delay(Timeout.Infinite);
                return MsmtReceiveResult.Accept();
            };
            Task<MsmtDisconnection> disconnected = WaitFor(connecting.Disconnected, args => args.Connection == connectingSide);

            await Assert.ThrowsAsync<TimeoutException>(() => connectingSide.Request("first"u8.ToArray()).WaitAsync(waitLimit));

            Assert.IsType<TimeoutException>((await disconnected).Exception);
            Assert.NotEqual(MsmtConnectionStatus.Connected, connectingSide.Status);
        }
    }

    /// <summary>A session is closed by the side that opened it when its negotiated lifetime ends, even with no traffic, and the other side sees it end without an error.</summary>
    [Fact]
    public async Task Session_LifetimeEnds_ConnectingSideClosesConnectionWithoutTraffic()
    {
        (IMsmtSessionPeer listening, IMsmtSessionPeer connecting, IMsmtConnection connectingSide, IMsmtConnection listeningSide, Task<MsmtDisconnection> listeningDisconnected, Task<MsmtDisconnection> connectingDisconnected) = await ConnectPair(
            connecting: options => options with { SessionLifetime = TimeSpan.FromSeconds(1) });
        await using (listening)
        await using (connecting)
        {
            Assert.Null((await connectingDisconnected).Exception);
            Assert.Null((await listeningDisconnected).Exception);
        }
    }

    /// <summary>A listener closes a session that goes silent once its negotiated lifetime, plus a short grace period, has ended.</summary>
    [Fact]
    public async Task Listener_SessionClientGoesSilent_ClosesAtLifetimeEnd()
    {
        (X509Certificate2 connectingCertificate, X509Certificate2 listeningCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtSessionPeer listening = new(Options(listeningCertificate, trustedAuthorities));
        Task<MsmtDisconnection> disconnected = WaitFor(listening.Disconnected);
        listening.StartListener(0, "127.0.0.1");

        using MsmtTlsChannel raw = await ConnectRawSession(listening.Listener!, connectingCertificate, trustedAuthorities, "1");

        Assert.Null((await disconnected).Exception);
    }

    /// <summary>A keep-alive keeps a session alive on the wire without counting as application traffic.</summary>
    [Fact]
    public async Task Session_Idle_SendsKeepAliveWithoutCountingAsActivity()
    {
        (IMsmtSessionPeer listening, IMsmtSessionPeer connecting, IMsmtConnection connectingSide, _, _, _) = await ConnectPair(
            connecting: options => options with { KeepAliveMinInterval = TimeSpan.FromMilliseconds(300), KeepAliveMaxInterval = TimeSpan.FromMilliseconds(300) });
        await using (listening)
        await using (connecting)
        {
            await connectingSide.Request("hello"u8.ToArray()).WaitAsync(waitLimit);
            MsmtConnection engine = (MsmtConnection)connectingSide;
            DateTime activityAt = engine.LastActivityUtc;

            using CancellationTokenSource timeout = new(waitLimit);
            while (engine.LastWireActivityUtc < activityAt + TimeSpan.FromMilliseconds(300))
            {
                await Task.Delay(50, timeout.Token);
            }

            Assert.Equal(activityAt, engine.LastActivityUtc);
            Assert.Equal(MsmtConnectionStatus.Connected, connectingSide.Status);
        }
    }

    /// <summary>Neither side of a session ever disconnects it automatically for being idle or unused - that is left entirely to the caller.</summary>
    [Fact]
    public async Task Session_OnlyKeepAlivesFlow_NeverAutomaticallyDisconnected()
    {
        (IMsmtSessionPeer listening, IMsmtSessionPeer connecting, IMsmtConnection connectingSide, IMsmtConnection listeningSide, _, _) = await ConnectPair(
            connecting: options => options with { KeepAliveMinInterval = TimeSpan.FromMilliseconds(200), KeepAliveMaxInterval = TimeSpan.FromMilliseconds(200) });
        await using (listening)
        await using (connecting)
        {
            await connectingSide.Request("hello"u8.ToArray()).WaitAsync(waitLimit);

            // Long enough to observe several keep-alive intervals with no automatic disconnection.
            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Equal(MsmtConnectionStatus.Connected, connectingSide.Status);
            Assert.Equal(MsmtConnectionStatus.Connected, listeningSide.Status);
        }
    }

    /// <summary>A subscriber that takes a long time to answer is never disconnected mid-message.</summary>
    [Fact]
    public async Task Listener_SlowSubscriber_DoesNotDisconnectMidMessage()
    {
        (IMsmtSessionPeer listening, IMsmtSessionPeer connecting, IMsmtConnection connectingSide, IMsmtConnection listeningSide, _, _) = await ConnectPair();
        await using (listening)
        await using (connecting)
        {
            listening.Receiver = (connection, payload, isResponseRequested) =>
            {
                Thread.Sleep(1500);
                return new ValueTask<MsmtReceiveResult?>(MsmtReceiveResult.Accept());
            };

            MsmtResponse response = await connectingSide.Request("hello"u8.ToArray()).WaitAsync(waitLimit);

            Assert.True(response.Success);
            Assert.Equal(MsmtConnectionStatus.Connected, listeningSide.Status);
        }
    }

    /// <summary>Disconnecting one side ends the connection on both without an error, and later sends throw.</summary>
    [Fact]
    public async Task Disconnect_OneSide_EndsBothAndRejectsLaterSends()
    {
        (IMsmtSessionPeer listening, IMsmtSessionPeer connecting, IMsmtConnection connectingSide, IMsmtConnection listeningSide, Task<MsmtDisconnection> listeningDisconnected, Task<MsmtDisconnection> connectingDisconnected) = await ConnectPair();
        await using (listening)
        await using (connecting)
        {
            await connectingSide.DisposeAsync();

            Assert.Null((await connectingDisconnected).Exception);
            Assert.Null((await listeningDisconnected).Exception);
            Assert.Equal(MsmtConnectionStatus.Disconnected, connectingSide.Status);
            Assert.Equal(MsmtConnectionStatus.Disconnected, listeningSide.Status);
            Assert.Throws<ObjectDisposedException>(() => connectingSide.Send("late"u8.ToArray()));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => listeningSide.Request("late"u8.ToArray()));
            Assert.Empty(connecting.Connections);
            Assert.Empty(listening.Connections);
        }
    }

    /// <summary>Dropping a connection while a request is waiting for its acknowledgement fails that request rather than leaving it hanging.</summary>
    [Fact]
    public async Task Drop_WhileRequestWaiting_FailsTheRequest()
    {
        (IMsmtSessionPeer listening, IMsmtSessionPeer connecting, IMsmtConnection connectingSide, IMsmtConnection listeningSide, _, _) = await ConnectPair();
        await using (listening)
        await using (connecting)
        {
            TaskCompletionSource received = new();
            listening.Receiver = async (connection, payload, isResponseRequested) =>
            {
                received.TrySetResult();
                await Task.Delay(Timeout.Infinite);
                return MsmtReceiveResult.Accept();
            };
            object tag = new();
            Task<MsmtResponse> request = connectingSide.Request("wait"u8.ToArray(), new MsmtSendOptions { Tag = tag });
            await received.Task.WaitAsync(waitLimit);

            listeningSide.Dispose();

            await Assert.ThrowsAnyAsync<IOException>(() => request.WaitAsync(waitLimit));
            Assert.Equal(MsmtSendStatus.Failed, connecting.GetPackage(tag)!.Status);
        }
    }

    /// <summary>A tagged send reports each stage of its progress, ending completed, and stays available afterwards.</summary>
    [Fact]
    public async Task Request_Tagged_ReportsProgressThroughCompletion()
    {
        (IMsmtSessionPeer listening, IMsmtSessionPeer connecting, IMsmtConnection connectingSide, _, _, _) = await ConnectPair();
        await using (listening)
        await using (connecting)
        {
            List<MsmtSendStatus> statuses = [];
            TaskCompletionSource completed = new();
            connecting.PackageChanged.Subscribe(args =>
            {
                if (args.Connection != connectingSide)
                {
                    return;
                }

                lock (statuses)
                {
                    statuses.Add(args.Status);
                }

                if (args.Status == MsmtSendStatus.Completed)
                {
                    completed.TrySetResult();
                }
            });

            object tag = new();
            await connectingSide.Request("hello"u8.ToArray(), new MsmtSendOptions { Tag = tag }).WaitAsync(waitLimit);
            await completed.Task.WaitAsync(waitLimit);

            Assert.Equal([MsmtSendStatus.Queued, MsmtSendStatus.Transmitting, MsmtSendStatus.PendingAcknowledgement, MsmtSendStatus.Completed], statuses);
            Assert.Equal(MsmtSendStatus.Completed, connecting.GetPackage(tag)!.Status);
            Assert.Empty(connecting.Packages);
        }
    }

    /// <summary>Cancelling a send still waiting in the queue cancels it without ever transmitting it, and later sends still work.</summary>
    [Fact]
    public async Task Cancel_QueuedSend_NeverTransmitsAndConnectionStaysUsable()
    {
        (IMsmtSessionPeer listening, IMsmtSessionPeer connecting, IMsmtConnection connectingSide, IMsmtConnection listeningSide, _, _) = await ConnectPair();
        await using (listening)
        await using (connecting)
        {
            int received = 0;
            TaskCompletionSource firstReceived = new();
            TaskCompletionSource releaseFirst = new();
            listening.Receiver = async (connection, payload, isResponseRequested) =>
            {
                if (Interlocked.Increment(ref received) == 1)
                {
                    firstReceived.TrySetResult();
                    await releaseFirst.Task;
                }

                return MsmtReceiveResult.Accept();
            };

            Task<MsmtResponse> blocking = connectingSide.Request("first"u8.ToArray());
            await firstReceived.Task.WaitAsync(waitLimit);
            object tag = new();
            Task<MsmtResponse> queued = connectingSide.Request("second"u8.ToArray(), new MsmtSendOptions { Tag = tag });

            connecting.GetPackage(tag)!.Cancel();
            releaseFirst.TrySetResult();
            await blocking.WaitAsync(waitLimit);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(waitLimit));
            Assert.Equal(MsmtSendStatus.Cancelled, connecting.GetPackage(tag)!.Status);
            Assert.Equal(1, Volatile.Read(ref received));
            Assert.True((await connectingSide.Request("third"u8.ToArray()).WaitAsync(waitLimit)).Success);
        }
    }

    /// <summary>Sends queued behind one in flight go out highest priority first, and first-in-first-out within a priority.</summary>
    [Fact]
    public async Task Send_QueuedWithPriorities_GoOutHighestPriorityFirst()
    {
        (IMsmtSessionPeer listening, IMsmtSessionPeer connecting, IMsmtConnection connectingSide, IMsmtConnection listeningSide, _, _) = await ConnectPair();
        await using (listening)
        await using (connecting)
        {
            List<string> order = [];
            TaskCompletionSource firstReceived = new();
            TaskCompletionSource releaseFirst = new();
            listening.Receiver = async (connection, payload, isResponseRequested) =>
            {
                string text = Encoding.ASCII.GetString(payload.Span);
                if (text == "first")
                {
                    firstReceived.TrySetResult();
                    await releaseFirst.Task;
                    return MsmtReceiveResult.Accept();
                }

                lock (order)
                {
                    order.Add(text);
                }

                return MsmtReceiveResult.Accept();
            };

            Task<MsmtResponse> blocking = connectingSide.Request("first"u8.ToArray());
            await firstReceived.Task.WaitAsync(waitLimit);
            Task<MsmtResponse>[] queued =
            [
                connectingSide.Request("low-a"u8.ToArray(), new MsmtSendOptions { Priority = 0 }),
                connectingSide.Request("high"u8.ToArray(), new MsmtSendOptions { Priority = 5 }),
                connectingSide.Request("mid"u8.ToArray(), new MsmtSendOptions { Priority = 2 }),
                connectingSide.Request("low-b"u8.ToArray(), new MsmtSendOptions { Priority = 0 }),
            ];
            releaseFirst.TrySetResult();
            await Task.WhenAll([blocking, .. queued]).WaitAsync(waitLimit);

            Assert.Equal(["high", "mid", "low-a", "low-b"], order);
        }
    }

    /// <summary>A <c>Connected</c> subscriber that throws leaves the connection unusable, so the listener drops it and reports the failure like any other connection that could not be established.</summary>
    [Fact]
    public async Task Listener_ConnectedSubscriberThrows_DropsConnectionAndReportsConnectFailed()
    {
        (X509Certificate2 connectingCertificate, X509Certificate2 listeningCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtSessionPeer listening = new(Options(listeningCertificate, trustedAuthorities));
        await using MsmtSessionPeer connecting = new(Options(connectingCertificate, trustedAuthorities));
        InvalidOperationException thrown = new("subscriber failure");
        listening.Connected.Subscribe(_ => throw thrown);
        Task<MsmtDisconnection> failed = WaitFor(listening.Disconnected);
        listening.StartListener(0, "127.0.0.1");

        IMsmtConnection connection = connecting.Connect(new MsmtNameTarget { Host = "127.0.0.1", Port = listening.Listener!.Port, ServerName = "127.0.0.1" });
        Assert.True(await connection.Wait().WaitAsync(waitLimit));

        Assert.Same(thrown, (await failed).Exception);
        Assert.Empty(listening.Connections);

        using CancellationTokenSource timeout = new(waitLimit);
        while (connection.Status == MsmtConnectionStatus.Connected)
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    /// <summary>A session peer cannot open a session with a message-mode peer's listener, which only serves message and message-with-rekeying connections.</summary>
    [Fact]
    public async Task Connect_ToPeerListener_IsRejected()
    {
        (X509Certificate2 connectingCertificate, X509Certificate2 listeningCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtMessagePeer peer = new(new MsmtMessagePeerOptions { Credentials = new MsmtCredentials { Identity = listeningCertificate, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtSessionPeer connecting = new(Options(connectingCertificate, trustedAuthorities));
        peer.StartListener(0, "127.0.0.1");

        IMsmtConnection connection = connecting.Connect(new MsmtNameTarget { Host = "127.0.0.1", Port = peer.Listener!.Port, ServerName = "127.0.0.1" });

        Assert.False(await connection.Wait().WaitAsync(waitLimit));
        Assert.IsType<InvalidOperationException>(((MsmtConnection)connection).CloseReason);
    }

    /// <summary>A remote peer that never answers the handshake fails to connect with a timeout, a target name that doesn't match the remote peer's certificate fails it with a different reason, and disposing a still-connecting attempt abandons it instead.</summary>
    [Fact]
    public async Task Connect_FailureModes_LeaveNoConnectionBehind()
    {
        (X509Certificate2 connectingCertificate, X509Certificate2 listeningCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        using TcpListener silent = new(IPAddress.Loopback, 0);
        silent.Start();
        await using MsmtSessionPeer connecting = new(Options(connectingCertificate, trustedAuthorities) with { HandshakeTimeout = TimeSpan.FromMilliseconds(300) });
        await using MsmtSessionPeer listening = new(Options(listeningCertificate, trustedAuthorities));
        listening.StartListener(0, "127.0.0.1");

        IMsmtConnection timedOut = connecting.Connect(new MsmtNameTarget { Host = "127.0.0.1", Port = ((IPEndPoint)silent.LocalEndpoint).Port, ServerName = "127.0.0.1" });
        Assert.False(await timedOut.Wait().WaitAsync(waitLimit));
        Assert.IsType<TimeoutException>(((MsmtConnection)timedOut).CloseReason);

        IMsmtConnection wrongName = connecting.Connect(new MsmtNameTarget { Host = "127.0.0.1", Port = listening.Listener!.Port, ServerName = "wrong.example.com" });
        Assert.False(await wrongName.Wait().WaitAsync(waitLimit));
        Assert.NotNull(((MsmtConnection)wrongName).CloseReason);

        IMsmtConnection abandoned = connecting.Connect(new MsmtNameTarget { Host = "127.0.0.1", Port = listening.Listener!.Port, ServerName = "127.0.0.1" });
        abandoned.Dispose();
        Assert.False(await abandoned.Wait().WaitAsync(waitLimit));

        // Removal from Connections happens once Terminate finishes reporting the disconnect, slightly after Wait resolves.
        using CancellationTokenSource timeout = new(waitLimit);
        while (connecting.Connections.Count > 0)
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    /// <summary>Disposing a session peer closes its connections, and it refuses further use.</summary>
    [Fact]
    public async Task DisposeAsync_ClosesConnectionsAndRefusesFurtherUse()
    {
        (IMsmtSessionPeer listening, IMsmtSessionPeer connecting, IMsmtConnection connectingSide, IMsmtConnection listeningSide, _, Task<MsmtDisconnection> connectingDisconnected) = await ConnectPair();

        await listening.DisposeAsync();
        await listening.DisposeAsync();
        await connectingDisconnected;
        Assert.Equal(MsmtConnectionStatus.Disconnected, listeningSide.Status);
        Assert.False(listening.IsListening);
        Assert.Throws<ObjectDisposedException>(() => listening.StartListener());

        await connecting.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => connecting.Connect(new MsmtNameTarget { Host = "127.0.0.1", Port = 1, ServerName = "127.0.0.1" }));
    }

    /// <summary>Starting on a port already in use fails and leaves the peer not listening.</summary>
    [Fact]
    public async Task StartListener_PortInUse_ThrowsAndLeavesPeerNotListening()
    {
        (_, X509Certificate2 listeningCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        using TcpListener occupier = new(IPAddress.Loopback, 0);
        occupier.Start();
        await using MsmtSessionPeer listening = new(Options(listeningCertificate, trustedAuthorities));
        listening.StartListener(0, "127.0.0.1");

        Assert.Throws<SocketException>(() => listening.StartListener(((IPEndPoint)occupier.LocalEndpoint).Port, "127.0.0.1"));

        Assert.False(listening.IsListening);
    }

    /// <summary>Options with a non-positive timeout or interval are rejected when a peer is created.</summary>
    [Fact]
    public void Options_NonPositiveValues_AreRejected()
    {
        (X509Certificate2 certificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        MsmtCredentials credentials = new() { Identity = certificate, TrustedAuthorities = trustedAuthorities };

        Assert.Throws<ArgumentOutOfRangeException>(() => new MsmtSessionPeer(new MsmtSessionPeerOptions { Credentials = credentials, HandshakeTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MsmtSessionPeer(new MsmtSessionPeerOptions { Credentials = credentials, MaximumSessionLifetime = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MsmtSessionPeer(new MsmtSessionPeerOptions { Credentials = credentials, StallTimeout = TimeSpan.FromSeconds(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MsmtSessionPeer(new MsmtSessionPeerOptions { Credentials = credentials, KeepAliveMinInterval = TimeSpan.FromMinutes(6), KeepAliveMaxInterval = TimeSpan.FromMinutes(5) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MsmtMessagePeer(new MsmtMessagePeerOptions { Credentials = credentials, ResponseTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MsmtMessagePeer(new MsmtMessagePeerOptions { Credentials = credentials, MaxIdleTime = TimeSpan.FromSeconds(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MsmtMessagePeer(new MsmtMessagePeerOptions { Credentials = credentials, RekeyLimit = 0 }));
    }

    private async Task<(IMsmtSessionPeer Listening, IMsmtSessionPeer Connecting, IMsmtConnection ConnectingSide, IMsmtConnection ListeningSide, Task<MsmtDisconnection> ListeningDisconnected, Task<MsmtDisconnection> ConnectingDisconnected)> ConnectPair(Func<MsmtSessionPeerOptions, MsmtSessionPeerOptions>? listening = null, Func<MsmtSessionPeerOptions, MsmtSessionPeerOptions>? connecting = null)
    {
        (X509Certificate2 connectingCertificate, X509Certificate2 listeningCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        MsmtSessionPeer listeningPeer = new(listening is null ? Options(listeningCertificate, trustedAuthorities) : listening(Options(listeningCertificate, trustedAuthorities)));
        MsmtSessionPeer connectingPeer = new(connecting is null ? Options(connectingCertificate, trustedAuthorities) : connecting(Options(connectingCertificate, trustedAuthorities)));
        TaskCompletionSource<MsmtDisconnection> listeningDisconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<MsmtDisconnection> connectingDisconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IMsmtConnection> accepted = WaitFor(listeningPeer.Connected);

        // Subscribed before either side starts, since a hot observable would otherwise miss a disconnect that
        // happens before the test gets around to waiting for it on a slow machine.
        listeningPeer.Disconnected.Subscribe(args => listeningDisconnected.TrySetResult(args));
        connectingPeer.Disconnected.Subscribe(args => connectingDisconnected.TrySetResult(args));
        listeningPeer.StartListener(0, "127.0.0.1");

        IMsmtConnection connectingSide = connectingPeer.Connect(new MsmtNameTarget { Host = "127.0.0.1", Port = listeningPeer.Listener!.Port, ServerName = "127.0.0.1" });
        Assert.True(await connectingSide.Wait().WaitAsync(waitLimit));
        return (listeningPeer, connectingPeer, connectingSide, await accepted, listeningDisconnected.Task.WaitAsync(TimeSpan.FromMinutes(1)), connectingDisconnected.Task.WaitAsync(TimeSpan.FromMinutes(1)));
    }

    private MsmtSessionPeerOptions Options(X509Certificate2 certificate, X509Certificate2Collection trustedAuthorities) => new()
    {
        Credentials = new MsmtCredentials { Identity = certificate, TrustedAuthorities = trustedAuthorities },
        RequireFullyQualifiedHostname = false,
    };

    private Task<T> WaitFor<T>(IObservable<T> source, Func<T, bool>? predicate = null)
    {
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Subscribe(value =>
        {
            if (predicate is null || predicate(value))
            {
                completion.TrySetResult(value);
            }
        });

        return completion.Task.WaitAsync(waitLimit);
    }

    private async Task<MsmtTlsChannel> ConnectRaw(MsmtTarget listener, X509Certificate2 certificate, X509Certificate2Collection trustedAuthorities)
    {
        Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(listener.Host, listener.Port);
        MsmtTlsChannel channel = new(socket, null);
        MsmtNameTarget target = new() { Host = listener.Host, Port = listener.Port, ServerName = listener.Host };
        await channel.ConnectAsClient(new MsmtTlsClient(new MsmtCredentials { Identity = certificate, TrustedAuthorities = trustedAuthorities }, target), CancellationToken.None);
        return channel;
    }

    private async Task<MsmtTlsChannel> ConnectRawSession(MsmtTarget listener, X509Certificate2 certificate, X509Certificate2Collection trustedAuthorities, string lifetimeSeconds)
    {
        MsmtTlsChannel channel = await ConnectRaw(listener, certificate, trustedAuthorities);
        byte[] payload = Encoding.ASCII.GetBytes(lifetimeSeconds);
        await WriteFrame(channel, new MsmtHeader { Version = MsmtHeader.SupportedVersion, Flags = MsmtMessageFlags.SessionModeNegotiation | MsmtMessageFlags.MessageSuccess, MessageId = 2, Length = (uint)payload.Length }, payload);
        (MsmtHeader response, _) = await ReadFrame(channel);
        Assert.Equal(MsmtMessageFlags.SessionModeAccepted, response.Flags);
        return channel;
    }

    private async Task WriteFrame(MsmtTlsChannel channel, MsmtHeader header, byte[] payload)
    {
        byte[] bytes = new byte[MsmtHeader.Size + payload.Length];
        header.Write(bytes);
        payload.CopyTo(bytes, MsmtHeader.Size);
        await channel.Write(bytes, CancellationToken.None);
    }

    private async Task<(MsmtHeader Header, byte[] Payload)> ReadFrame(MsmtTlsChannel channel)
    {
        async Task<byte[]> ReadExactly(int count)
        {
            byte[] buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int chunk = await channel.Read(buffer.AsMemory(read), CancellationToken.None);
                Assert.NotEqual(0, chunk);
                read += chunk;
            }

            return buffer;
        }

        MsmtHeader header = MsmtHeader.Read(await ReadExactly(MsmtHeader.Size).WaitAsync(waitLimit));
        return (header, await ReadExactly((int)header.Length));
    }
}
