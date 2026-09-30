namespace BlueHeighliner.Msmt.Tests.Integration;

/// <summary>Integration tests for <see cref="MsmtMessagePeer"/>, which sends to and receives from targets directly and keeps its connections internal.</summary>
public sealed class MsmtMessagePeerTests
{
    private readonly TimeSpan waitLimit = TimeSpan.FromSeconds(10);

    /// <summary>A request in message mode reaches the listening peer's receiver, with the sender's address and verified identity, and returns its acknowledgement.</summary>
    [Fact]
    public async Task Request_MessageMode_DeliversAndReturnsAcknowledgement()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtMessagePeer peerB = Listener(certificateB, trustedAuthorities);
        await using MsmtMessagePeer peerA = Sender(certificateA, trustedAuthorities);
        TaskCompletionSource<(byte[] Payload, bool IsResponseRequested, MsmtIdentity Identity, MsmtTarget Source)> received = new();
        peerB.Receiver = (source, identity, payload, responder) =>
        {
            using (payload)
            {
                received.TrySetResult((payload.Memory.ToArray(), responder is not null, identity, source));
            }

            responder!.Accept("ack"u8.ToArray());
        };
        peerB.StartListener(0, "127.0.0.1");

        MsmtResponse response = await peerA.Request(Target(peerB), "hello"u8.ToArray()).WaitAsync(waitLimit);

        Assert.True(response.Success);
        Assert.Equal("ack", Encoding.ASCII.GetString(response.Payload.Memory.Span));
        (byte[] payload, bool isResponseRequested, MsmtIdentity identity, MsmtTarget source) = await received.Task.WaitAsync(waitLimit);
        Assert.Equal("hello", Encoding.ASCII.GetString(payload));
        Assert.True(isResponseRequested);
        Assert.Equal(MsmtIdentity.FromCertificate(certificateA), identity);
        Assert.Equal("127.0.0.1", source.Host);
    }

    /// <summary>A plain send is delivered without asking for an acknowledgement, so its receiver is given no responder.</summary>
    [Fact]
    public async Task Send_NoAcknowledgementRequested_IsDeliveredWithoutResponder()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtMessagePeer peerB = Listener(certificateB, trustedAuthorities);
        await using MsmtMessagePeer peerA = Sender(certificateA, trustedAuthorities);
        TaskCompletionSource<bool> received = new();
        peerB.Receiver = (source, identity, payload, responder) =>
        {
            payload.Dispose();
            received.TrySetResult(responder is null);
        };
        peerB.StartListener(0, "127.0.0.1");

        peerA.Send(Target(peerB), "hello"u8.ToArray());

        Assert.True(await received.Task.WaitAsync(waitLimit));
    }

    /// <summary>A message can be answered after its receiver has returned, and the connection stays open until it is, even in message mode where it otherwise closes after one exchange.</summary>
    [Fact]
    public async Task Request_AnsweredAfterReceiverReturned_ReachesSender()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtMessagePeer peerB = Listener(certificateB, trustedAuthorities);
        await using MsmtMessagePeer peerA = Sender(certificateA, trustedAuthorities);
        TaskCompletionSource<IMsmtResponder> received = new();
        peerB.Receiver = (source, identity, payload, responder) =>
        {
            payload.Dispose();
            received.TrySetResult(responder!);
        };
        peerB.StartListener(0, "127.0.0.1");

        Task<MsmtResponse> request = peerA.Request(Target(peerB), "hello"u8.ToArray());
        IMsmtResponder responder = await received.Task.WaitAsync(waitLimit);
        await Task.Delay(200);
        Assert.False(request.IsCompleted);
        responder.Reject("later"u8.ToArray());

        MsmtResponse response = await request.WaitAsync(waitLimit);

        Assert.False(response.Success);
        Assert.Equal("later", Encoding.ASCII.GetString(response.Payload.Memory.Span));
        Assert.Throws<InvalidOperationException>(() => responder.Accept());
        await WaitUntil(() => peerB.AcceptedCount == 0);
    }

    /// <summary>A receiver that throws is reported through the peer's exceptions without affecting later messages.</summary>
    [Fact]
    public async Task Receiver_Throws_IsReportedAndLaterMessagesStillDeliver()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtMessagePeer peerB = Listener(certificateB, trustedAuthorities);
        await using MsmtMessagePeer peerA = Sender(certificateA, trustedAuthorities);
        InvalidOperationException thrown = new("receiver failure");
        Task<Exception> reported = WaitFor(peerB.Exceptions);
        TaskCompletionSource<string> second = new();
        peerB.Receiver = (source, identity, payload, responder) =>
        {
            string text = Encoding.ASCII.GetString(payload.Memory.Span);
            payload.Dispose();

            if (text == "first")
            {
                throw thrown;
            }

            second.TrySetResult(text);
        };
        peerB.StartListener(0, "127.0.0.1");

        peerA.Send(Target(peerB), "first"u8.ToArray());
        peerA.Send(Target(peerB), "second"u8.ToArray());

        Assert.Same(thrown, await reported);
        Assert.Equal("second", await second.Task.WaitAsync(waitLimit));
    }

    /// <summary>A subscriber that throws while a tagged send's progress is published is reported through the peer's exceptions, and neither the send nor other subscribers are affected.</summary>
    [Fact]
    public async Task PackageChanged_SubscriberThrows_IsReportedAndSendStillCompletes()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtMessagePeer peerB = Listener(certificateB, trustedAuthorities);
        await using MsmtMessagePeer peerA = Sender(certificateA, trustedAuthorities);
        peerB.StartListener(0, "127.0.0.1");
        InvalidOperationException thrown = new("subscriber failure");
        Task<Exception> reported = WaitFor(peerA.Exceptions);
        Task<MsmtPackageChange> completed = WaitFor(peerA.PackageChanged, args => args.Status == MsmtSendStatus.Completed);
        peerA.PackageChanged.Subscribe(_ => throw thrown);

        peerA.Send(Target(peerB), "hello"u8.ToArray(), new MsmtSendOptions { Tag = new object() });

        Assert.Same(thrown, await reported);
        await completed;
    }

    /// <summary>Every message in message mode opens a connection of its own, which is gone again once the exchange ends.</summary>
    [Fact]
    public async Task Send_MessageMode_ConnectionPerMessage()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtMessagePeer peerB = Listener(certificateB, trustedAuthorities);
        await using MsmtMessagePeer peerA = Sender(certificateA, trustedAuthorities);
        int accepted = 0;
        peerB.Receiver = (source, identity, payload, responder) =>
        {
            payload.Dispose();
            Interlocked.Increment(ref accepted);
            responder!.Accept();
        };
        peerB.StartListener(0, "127.0.0.1");

        for (int index = 0; index < 3; index++)
        {
            Assert.True((await peerA.Request(Target(peerB), "hello"u8.ToArray()).WaitAsync(waitLimit)).Success);
        }

        Assert.Equal(3, accepted);
        await WaitUntil(() => peerB.AcceptedCount == 0);
    }

    /// <summary>Message-with-rekeying mode reuses one connection for many messages.</summary>
    [Fact]
    public async Task Send_MessageWithRekeying_ReusesOneConnection()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtMessagePeer peerB = Listener(certificateB, trustedAuthorities, options => options with { RekeyLimit = 100 });
        await using MsmtMessagePeer peerA = Sender(certificateA, trustedAuthorities, options => options with { RekeyLimit = 100 });
        int maxAccepted = 0;
        peerB.Receiver = (source, identity, payload, responder) =>
        {
            payload.Dispose();
            maxAccepted = Math.Max(maxAccepted, peerB.AcceptedCount);
            responder!.Accept();
        };
        peerB.StartListener(0, "127.0.0.1");

        for (int index = 0; index < 10; index++)
        {
            Assert.True((await peerA.Request(Target(peerB), Encoding.ASCII.GetBytes($"m{index}")).WaitAsync(waitLimit)).Success);
        }

        Assert.Equal(1, maxAccepted);
        Assert.Equal(1, peerB.AcceptedCount);
    }

    /// <summary>The sender resets its connection after its rekey limit, and the listener does the same after its own, without any message failing.</summary>
    [Theory]
    [InlineData(2, 100)]
    [InlineData(100, 2)]
    public async Task Send_RekeyLimitReachedOnEitherSide_NextMessagesStillSucceed(int senderLimit, int listenerLimit)
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtMessagePeer peerB = Listener(certificateB, trustedAuthorities, options => options with { RekeyLimit = listenerLimit });
        await using MsmtMessagePeer peerA = Sender(certificateA, trustedAuthorities, options => options with { RekeyLimit = senderLimit });
        int received = 0;
        peerB.Receiver = (source, identity, payload, responder) =>
        {
            payload.Dispose();
            Interlocked.Increment(ref received);
            responder!.Accept();
        };
        peerB.StartListener(0, "127.0.0.1");

        for (int index = 0; index < 7; index++)
        {
            Assert.True((await peerA.Request(Target(peerB), "hello"u8.ToArray()).WaitAsync(waitLimit)).Success);
            await Task.Delay(300);
        }

        Assert.Equal(7, received);
    }

    /// <summary>A request to a target nobody is listening on fails, and its tagged package reports the failure with the exception.</summary>
    [Fact]
    public async Task Request_TargetUnreachable_FailsAndTaggedPackageReportsFailure()
    {
        (X509Certificate2 certificateA, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        using TcpListener closed = new(IPAddress.Loopback, 0);
        closed.Start();
        int port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        await using MsmtMessagePeer peerA = Sender(certificateA, trustedAuthorities);
        Task<MsmtPackageChange> failed = WaitFor(peerA.PackageChanged, args => args.Status == MsmtSendStatus.Failed);
        object tag = new();

        await Assert.ThrowsAsync<SocketException>(() => peerA.Request(new MsmtTarget { Host = "127.0.0.1", Port = port }, "hello"u8.ToArray(), new MsmtSendOptions { Tag = tag }).WaitAsync(waitLimit));

        Assert.IsType<SocketException>((await failed).Exception);
        Assert.Equal(MsmtSendStatus.Failed, peerA.GetPackage(tag)!.Status);
    }

    /// <summary>A tagged send reports each stage of its progress, ending completed, and stays available even after the peer's pooled sender for that target has been evicted.</summary>
    [Fact]
    public async Task Send_Tagged_PackageSurvivesEvictionOfItsSender()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtMessagePeer peerB = Listener(certificateB, trustedAuthorities);
        await using MsmtMessagePeer peerA = Sender(certificateA, trustedAuthorities, options => options with { MaxIdleTime = TimeSpan.FromMilliseconds(300) });
        peerB.StartListener(0, "127.0.0.1");
        List<MsmtSendStatus> statuses = [];
        Task<MsmtPackageChange> completed = WaitFor(peerA.PackageChanged, args =>
        {
            lock (statuses)
            {
                statuses.Add(args.Status);
            }

            return args.Status == MsmtSendStatus.Completed;
        });
        object tag = new();

        await peerA.Request(Target(peerB), "hello"u8.ToArray(), new MsmtSendOptions { Tag = tag }).WaitAsync(waitLimit);
        await completed;
        await WaitUntil(() => peerA.SenderCount == 0);

        Assert.Equal([MsmtSendStatus.Queued, MsmtSendStatus.Transmitting, MsmtSendStatus.PendingAcknowledgement, MsmtSendStatus.Completed], statuses);
        IMsmtPackage package = peerA.GetPackage(tag)!;
        Assert.Equal(MsmtSendStatus.Completed, package.Status);
        Assert.Empty(peerA.Packages);
        package.Cancel();
        Assert.Equal(MsmtSendStatus.Completed, package.Status);
    }

    /// <summary>Cancelling a queued send never transmits it, and sends queued behind go out highest priority first.</summary>
    [Fact]
    public async Task Send_QueuedBehindSlowOne_CancelAndPriorityAreHonored()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtMessagePeer peerB = Listener(certificateB, trustedAuthorities);
        await using MsmtMessagePeer peerA = Sender(certificateA, trustedAuthorities);
        List<string> order = [];
        TaskCompletionSource firstReceived = new();
        TaskCompletionSource releaseFirst = new();
        peerB.Receiver = (source, identity, payload, responder) =>
        {
            string text = Encoding.ASCII.GetString(payload.Memory.Span);
            payload.Dispose();
            if (text == "first")
            {
                firstReceived.TrySetResult();
                _ = releaseFirst.Task.ContinueWith(_ => responder!.Accept(), TaskScheduler.Default);
                return;
            }

            lock (order)
            {
                order.Add(text);
            }

            responder!.Accept();
        };
        peerB.StartListener(0, "127.0.0.1");

        Task<MsmtResponse> blocking = peerA.Request(Target(peerB), "first"u8.ToArray());
        await firstReceived.Task.WaitAsync(waitLimit);
        object cancelTag = new();
        Task<MsmtResponse> low = peerA.Request(Target(peerB), "low"u8.ToArray(), new MsmtSendOptions { Priority = 0 });
        Task<MsmtResponse> cancelled = peerA.Request(Target(peerB), "cancelled"u8.ToArray(), new MsmtSendOptions { Tag = cancelTag });
        Task<MsmtResponse> high = peerA.Request(Target(peerB), "high"u8.ToArray(), new MsmtSendOptions { Priority = 5 });

        peerA.GetPackage(cancelTag)!.Cancel();
        releaseFirst.TrySetResult();
        await Task.WhenAll(blocking, low, high).WaitAsync(waitLimit);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(waitLimit));
        Assert.Equal(MsmtSendStatus.Cancelled, peerA.GetPackage(cancelTag)!.Status);
        Assert.Equal(["high", "low"], order);
    }

    /// <summary>Cancelling a request already written closes its connection and fails it as cancelled, and later requests open a new one.</summary>
    [Fact]
    public async Task Request_CancelledInFlight_ThrowsCancelledAndLaterRequestsWork()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtMessagePeer peerB = Listener(certificateB, trustedAuthorities);
        await using MsmtMessagePeer peerA = Sender(certificateA, trustedAuthorities);
        int received = 0;
        TaskCompletionSource firstReceived = new();
        peerB.Receiver = (source, identity, payload, responder) =>
        {
            payload.Dispose();
            if (Interlocked.Increment(ref received) == 1)
            {
                firstReceived.TrySetResult();
                return;
            }

            responder!.Accept();
        };
        peerB.StartListener(0, "127.0.0.1");
        using CancellationTokenSource cancellation = new();

        Task<MsmtResponse> request = peerA.Request(Target(peerB), "first"u8.ToArray(), cancellation: cancellation.Token);
        await firstReceived.Task.WaitAsync(waitLimit);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(waitLimit));
        Assert.True((await peerA.Request(Target(peerB), "second"u8.ToArray()).WaitAsync(waitLimit)).Success);
    }

    /// <summary>A message the remote application never acknowledges fails with a timeout once the response timeout passes, without blocking later sends.</summary>
    [Fact]
    public async Task Request_NeverAcknowledged_FailsWithTimeoutAndLaterSendsStillWork()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtMessagePeer peerB = Listener(certificateB, trustedAuthorities);
        await using MsmtMessagePeer peerA = Sender(certificateA, trustedAuthorities, options => options with { ResponseTimeout = TimeSpan.FromMilliseconds(500) });
        int received = 0;
        peerB.Receiver = (source, identity, payload, responder) =>
        {
            payload.Dispose();
            if (Interlocked.Increment(ref received) == 1)
            {
                return;
            }

            responder!.Accept();
        };
        peerB.StartListener(0, "127.0.0.1");

        await Assert.ThrowsAsync<TimeoutException>(() => peerA.Request(Target(peerB), "first"u8.ToArray()).WaitAsync(waitLimit));

        Assert.True((await peerA.Request(Target(peerB), "second"u8.ToArray()).WaitAsync(waitLimit)).Success);
    }

    /// <summary>A remote peer that never answers the handshake fails the send with a timeout.</summary>
    [Fact]
    public async Task Request_RemoteNeverAnswersHandshake_FailsWithTimeout()
    {
        (X509Certificate2 certificateA, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        using TcpListener silent = new(IPAddress.Loopback, 0);
        silent.Start();
        await using MsmtMessagePeer peerA = Sender(certificateA, trustedAuthorities, options => options with { HandshakeTimeout = TimeSpan.FromMilliseconds(300) });
        MsmtTarget target = new() { Host = "127.0.0.1", Port = ((IPEndPoint)silent.LocalEndpoint).Port };

        await Assert.ThrowsAsync<TimeoutException>(() => peerA.Request(target, "hello"u8.ToArray()).WaitAsync(waitLimit));
    }

    /// <summary>A peer refuses to serve a session, and connecting to a session-only listener as a peer fails, reported by that listener.</summary>
    [Fact]
    public async Task Request_ToSessionOnlyListener_FailsAndListenerReportsIt()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtSessionPeer sessionPeer = new(new MsmtSessionPeerOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtMessagePeer peerA = Sender(certificateA, trustedAuthorities);
        Task<MsmtDisconnection> failed = WaitFor(sessionPeer.Disconnected);
        sessionPeer.StartListener(0, "127.0.0.1");

        await Assert.ThrowsAnyAsync<Exception>(() => peerA.Request(new MsmtTarget { Host = "127.0.0.1", Port = sessionPeer.Listener!.Port }, "hello"u8.ToArray()).WaitAsync(waitLimit));

        Assert.IsType<NotSupportedException>((await failed).Exception);
    }

    /// <summary>A listening peer drops a client that never handshakes, without exposing a connection, and reports the failure through its exceptions.</summary>
    [Fact]
    public async Task Listener_ClientNeverHandshakes_IsDroppedAndListenerKeepsServing()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtMessagePeer peerB = Listener(certificateB, trustedAuthorities, options => options with { HandshakeTimeout = TimeSpan.FromMilliseconds(300) });
        await using MsmtMessagePeer peerA = Sender(certificateA, trustedAuthorities);
        peerB.StartListener(0, "127.0.0.1");
        Task<Exception> reported = WaitFor(peerB.Exceptions);

        using TcpClient stalled = new();
        await stalled.ConnectAsync(IPAddress.Loopback, peerB.Listener!.Port);
        Assert.True((await peerA.Request(Target(peerB), "hello"u8.ToArray()).WaitAsync(waitLimit)).Success);

        using NetworkStream stream = stalled.GetStream();
        Assert.Equal(0, await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(waitLimit));
        Assert.IsType<TimeoutException>(await reported);
    }

    /// <summary>A subscriber that takes longer than the idle time to answer is never disconnected mid-message.</summary>
    [Fact]
    public async Task Listener_SlowSubscriberLongerThanMaxIdleTime_IsNotDisconnectedMidMessage()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtMessagePeer peerB = Listener(certificateB, trustedAuthorities, options => options with { MaxIdleTime = TimeSpan.FromMilliseconds(50) });
        await using MsmtMessagePeer peerA = Sender(certificateA, trustedAuthorities);
        peerB.Receiver = (source, identity, payload, responder) =>
        {
            payload.Dispose();
            Thread.Sleep(1500);
            responder!.Accept();
        };
        peerB.StartListener(0, "127.0.0.1");

        Assert.True((await peerA.Request(Target(peerB), "hello"u8.ToArray()).WaitAsync(waitLimit)).Success);
    }

    /// <summary>Exceeding the sender's connection count evicts its least recently used per-target sender.</summary>
    [Fact]
    public async Task Send_ExceedsMaxConnectionCount_EvictsLeastRecentlyUsedSender()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtMessagePeer peerA = Sender(certificateA, trustedAuthorities, options => options with { MaxConnectionCount = 2, MaxIdleTime = null });
        List<MsmtMessagePeer> listeners = [];
        try
        {
            for (int index = 0; index < 3; index++)
            {
                MsmtMessagePeer listener = Listener(certificateB, trustedAuthorities);
                listener.StartListener(0, "127.0.0.1");
                listeners.Add(listener);
                await peerA.Request(Target(listener), "hello"u8.ToArray()).WaitAsync(waitLimit);
                await Task.Delay(50);
            }

            await WaitUntil(() => peerA.SenderCount == 2);
        }
        finally
        {
            foreach (MsmtMessagePeer listener in listeners)
            {
                await listener.DisposeAsync();
            }
        }
    }

    /// <summary>Exceeding the listener's connection count disconnects its accepted connection with the oldest traffic.</summary>
    [Fact]
    public async Task Listener_ExceedsMaxConnectionCount_DisconnectsOldestAcceptedConnection()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtMessagePeer peerB = Listener(certificateB, trustedAuthorities, options => options with { MaxConnectionCount = 2, MaxIdleTime = null, RekeyLimit = 100 });
        peerB.StartListener(0, "127.0.0.1");
        List<MsmtMessagePeer> senders = [];
        try
        {
            for (int index = 0; index < 3; index++)
            {
                MsmtMessagePeer sender = Sender(certificateA, trustedAuthorities, options => options with { RekeyLimit = 100, MaxIdleTime = null });
                senders.Add(sender);
                await sender.Request(Target(peerB), "hello"u8.ToArray()).WaitAsync(waitLimit);
                await Task.Delay(50);
            }

            await WaitUntil(() => peerB.AcceptedCount == 2);
        }
        finally
        {
            foreach (MsmtMessagePeer sender in senders)
            {
                await sender.DisposeAsync();
            }
        }
    }

    /// <summary>A pooled connection kept open in rekeying mode is discarded once unused for the idle time, and the next send opens a new one.</summary>
    [Fact]
    public async Task Send_RekeyingConnectionUnusedForMaxIdleTime_IsDisconnectedAndNextSendReconnects()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtMessagePeer peerB = Listener(certificateB, trustedAuthorities, options => options with { RekeyLimit = 100 });
        await using MsmtMessagePeer peerA = Sender(certificateA, trustedAuthorities, options => options with { RekeyLimit = 100, MaxIdleTime = TimeSpan.FromMilliseconds(500) });
        peerB.StartListener(0, "127.0.0.1");

        await peerA.Request(Target(peerB), "one"u8.ToArray()).WaitAsync(waitLimit);
        await WaitUntil(() => peerB.AcceptedCount == 0 && peerA.SenderCount == 0);

        Assert.True((await peerA.Request(Target(peerB), "two"u8.ToArray()).WaitAsync(waitLimit)).Success);
    }

    /// <summary>Starting on a port already in use fails and leaves the peer not listening, and stopping leaves accepted connections alone.</summary>
    [Fact]
    public async Task StartListener_PortInUse_ThrowsAndLeavesPeerNotListening()
    {
        (X509Certificate2 certificateA, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        using TcpListener occupier = new(IPAddress.Loopback, 0);
        occupier.Start();
        await using MsmtMessagePeer peer = Sender(certificateA, trustedAuthorities);
        peer.StartListener(0, "127.0.0.1");
        Assert.True(peer.IsListening);

        Assert.Throws<SocketException>(() => peer.StartListener(((IPEndPoint)occupier.LocalEndpoint).Port, "127.0.0.1"));

        Assert.False(peer.IsListening);
        Assert.Null(peer.Listener);
        peer.StopListener();
    }

    /// <summary>Disposing a peer more than once does not throw, cancels what is queued, and refuses further use.</summary>
    [Fact]
    public async Task Dispose_MoreThanOnce_CancelsQueuedSendsAndRefusesFurtherUse()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtMessagePeer peerB = Listener(certificateB, trustedAuthorities);
        MsmtMessagePeer peerA = Sender(certificateA, trustedAuthorities);
        TaskCompletionSource received = new();
        peerB.Receiver = (source, identity, payload, responder) =>
        {
            payload.Dispose();
            received.TrySetResult();
        };
        peerB.StartListener(0, "127.0.0.1");
        Task<MsmtResponse> inFlight = peerA.Request(Target(peerB), "first"u8.ToArray());
        await received.Task.WaitAsync(waitLimit);
        Task<MsmtResponse> queued = peerA.Request(Target(peerB), "second"u8.ToArray());

        await peerA.DisposeAsync();
        peerA.Dispose();
        await peerA.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => inFlight.WaitAsync(waitLimit));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(waitLimit));
        Assert.Throws<ObjectDisposedException>(() => peerA.Send(Target(peerB), "x"u8.ToArray()));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => peerA.Request(Target(peerB), "x"u8.ToArray()));
        Assert.Throws<ObjectDisposedException>(() => peerA.StartListener(0, "127.0.0.1"));
    }

    /// <summary>A peer disposed while a message is still being handled completes without throwing.</summary>
    [Fact]
    public async Task DisposeAsync_WhilePendingReceive_CompletesWithoutThrowing()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        MsmtMessagePeer peerB = Listener(certificateB, trustedAuthorities);
        await using MsmtMessagePeer peerA = Sender(certificateA, trustedAuthorities);
        TaskCompletionSource received = new();
        peerB.Receiver = (source, identity, payload, responder) =>
        {
            payload.Dispose();
            received.TrySetResult();
        };
        peerB.StartListener(0, "127.0.0.1");
        Task<MsmtResponse> response = peerA.Request(Target(peerB), "hello"u8.ToArray());
        await received.Task.WaitAsync(waitLimit);

        await peerB.DisposeAsync().AsTask().WaitAsync(waitLimit);

        await Assert.ThrowsAnyAsync<Exception>(() => response.WaitAsync(waitLimit));
    }

    /// <summary>A pool-rented buffer trimmed with <see cref="MsmtMemoryOwnerExtensions.Slice"/> is delivered with exactly the sliced bytes, not the pool's larger allocation.</summary>
    [Fact]
    public async Task Request_SlicedRentedOwner_DeliversExactlyTheSlicedBytes()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtMessagePeer peerB = Listener(certificateB, trustedAuthorities);
        await using MsmtMessagePeer peerA = Sender(certificateA, trustedAuthorities);
        TaskCompletionSource<byte[]> received = new();
        peerB.Receiver = (source, identity, payload, responder) =>
        {
            received.TrySetResult(payload.Memory.ToArray());
            payload.Dispose();
            responder!.Accept();
        };
        peerB.StartListener(0, "127.0.0.1");
        byte[] payload = "hello"u8.ToArray();
        IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(payload.Length + 100).Slice(0, payload.Length);
        payload.CopyTo(owner.Memory);

        MsmtResponse response = await peerA.Request(Target(peerB), owner).WaitAsync(waitLimit);

        Assert.True(response.Success);
        Assert.Equal(payload, await received.Task.WaitAsync(waitLimit));
    }

    private MsmtMessagePeer Listener(X509Certificate2 certificate, X509Certificate2Collection trustedAuthorities, Func<MsmtMessagePeerOptions, MsmtMessagePeerOptions>? configure = null)
    {
        MsmtMessagePeerOptions options = new() { Credentials = new MsmtCredentials { Identity = certificate, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false };
        return new MsmtMessagePeer(configure is null ? options : configure(options));
    }

    private MsmtMessagePeer Sender(X509Certificate2 certificate, X509Certificate2Collection trustedAuthorities, Func<MsmtMessagePeerOptions, MsmtMessagePeerOptions>? configure = null)
    {
        MsmtMessagePeerOptions options = new() { Credentials = new MsmtCredentials { Identity = certificate, TrustedAuthorities = trustedAuthorities } };
        return new MsmtMessagePeer(configure is null ? options : configure(options));
    }

    private MsmtNameTarget Target(MsmtMessagePeer peer) => new() { Host = peer.Listener!.Host, Port = peer.Listener!.Port, ServerName = peer.Listener!.Host };

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

    private async Task WaitUntil(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(waitLimit);
        while (!condition())
        {
            await Task.Delay(50, timeout.Token);
        }
    }
}
