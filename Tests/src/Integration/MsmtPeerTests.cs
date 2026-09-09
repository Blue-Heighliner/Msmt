namespace BlueHeighliner.Msmt.Tests.Integration;

/// <summary>Integration tests for <see cref="MsmtPeer"/> exchanging real MSMT messages over TLS.</summary>
public sealed class MsmtPeerTests
{
    /// <summary>A peer sending to another peer's listener creates an on-demand connection and both sides receive the exchange.</summary>
    [Fact]
    public async Task Send_ToAnotherPeer_DeliversMessageAndAcknowledgement()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        // peerB is declared (and therefore disposed) after peerA, since peerA's outgoing Session Mode
        // connection must close before peerB's listener tries to await its accepted connection's teardown.
        await using MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtPeer peerA = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities } });

        TaskCompletionSource<MsmtReceivedEventArgs> receivedByB = new();
        peerB.Received.Subscribe(args => receivedByB.TrySetResult(args));

        peerB.StartListener(0, "127.0.0.1");

        Task<MsmtResponse> responseTask = peerA.Request(peerB.Listener!, "hello"u8.ToArray());

        MsmtReceivedEventArgs received = await receivedByB.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hello"u8.ToArray(), received.Payload.Memory.ToArray());

        MsmtResponse response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(response.Success);
    }

    /// <summary>While a Session Mode connection is open, both peers' <see cref="MsmtPeer.ActiveConnections"/> lists reflect it.</summary>
    [Fact]
    public async Task ActiveConnections_WhileSessionOpen_ReflectsActiveConnections()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtPeer peerA = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.Session,
            SessionLifetime = TimeSpan.FromSeconds(30),
        });

        // Waiting for B to receive the message - rather than for either side's Connected - guarantees both
        // sides' handshakes have fully completed, since a message can only be exchanged afterward.
        TaskCompletionSource<MsmtReceivedEventArgs> receivedByB = new();
        peerB.Received.Subscribe(args => receivedByB.TrySetResult(args));

        peerB.StartListener(0, "127.0.0.1");

        peerA.Send(peerB.Listener!, "hello"u8.ToArray());

        await receivedByB.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(peerA.ActiveConnections);
        Assert.Single(peerB.ActiveConnections);
    }

    /// <summary>
    /// A connection is added to <see cref="MsmtPeer.ActiveConnections"/> before <see
    /// cref="IMsmtPeer.Connected"/> is raised for it - so the list is already up to date from inside the
    /// subscriber itself.
    /// </summary>
    [Fact]
    public async Task ActiveConnections_DuringConnected_AlreadyReflectsIt()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtPeer peerA = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities } });

        TaskCompletionSource<bool> activeDuringConnected = new();
        peerA.Connected.Subscribe(args => activeDuringConnected.TrySetResult(peerA.ActiveConnections.Contains(args.Connection)));

        peerB.StartListener(0, "127.0.0.1");

        peerA.Send(peerB.Listener!, "hello"u8.ToArray());

        Assert.True(await activeDuringConnected.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// A peer's outgoing link raises <see cref="IMsmtPeer.Linking"/>, then <see cref="IMsmtPeer.Linked"/>
    /// once its handshake completes, and finally <see cref="IMsmtPeer.Unlinked"/> once the connection
    /// tears down (Message Mode closes it after every send).
    /// </summary>
    [Fact]
    public async Task Send_ToAnotherPeer_RaisesLinkingThenLinkedThenUnlinked()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtPeer peerA = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities } });

        TaskCompletionSource<MsmtLinkingEventArgs> linking = new();
        TaskCompletionSource<MsmtLinkedEventArgs> linked = new();
        TaskCompletionSource<MsmtUnlinkedEventArgs> unlinked = new();
        peerA.Linking.Subscribe(args => linking.TrySetResult(args));
        peerA.Linked.Subscribe(args => linked.TrySetResult(args));
        peerA.Unlinked.Subscribe(args => unlinked.TrySetResult(args));

        peerB.StartListener(0, "127.0.0.1");

        peerA.Send(peerB.Listener!, "hello"u8.ToArray());

        MsmtLinkingEventArgs linkingArgs = await linking.Task.WaitAsync(TimeSpan.FromSeconds(5));
        MsmtLinkedEventArgs linkedArgs = await linked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        MsmtUnlinkedEventArgs unlinkedArgs = await unlinked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(linkingArgs.Link, linkedArgs.Link);
        Assert.Same(linkedArgs.Link, unlinkedArgs.Link);
        Assert.Equal(MsmtLinkType.Sender, linkedArgs.Link.Kind);
    }

    /// <summary>
    /// The receiving side's <see cref="IMsmtPeer.Linking"/>/<see cref="IMsmtPeer.Linked"/>/<see
    /// cref="IMsmtPeer.Connected"/> ordering mirrors the sending side exactly: <see
    /// cref="IMsmtPeer.Connected"/> only fires once the accepted link's handshake has completed (<see
    /// cref="IMsmtPeer.Linked"/> already raised for it), not as soon as it is merely accepted.
    /// </summary>
    [Fact]
    public async Task Send_ToAnotherPeer_ReceiverRaisesLinkingThenLinkedThenConnected()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtPeer peerA = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities } });

        List<string> order = [];
        Lock orderLock = new();
        TaskCompletionSource connected = new();
        peerB.Linking.Subscribe(_ => { lock (orderLock) { order.Add("Linking"); } });
        peerB.Linked.Subscribe(_ => { lock (orderLock) { order.Add("Linked"); } });
        peerB.Connected.Subscribe(_ =>
        {
            lock (orderLock)
            {
                order.Add("Connected");
            }

            connected.TrySetResult();
        });

        peerB.StartListener(0, "127.0.0.1");

        peerA.Send(peerB.Listener!, "hello"u8.ToArray());

        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (orderLock)
        {
            Assert.Equal(["Linking", "Linked", "Connected"], order);
        }
    }

    /// <summary><see cref="MsmtPeer.GetActiveConnection"/> returns the connection matching a target's host and port, or <see langword="null"/> if none exists.</summary>
    [Fact]
    public async Task GetActiveConnection_MatchingConnectionExists_ReturnsItOtherwiseNull()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtPeer peerA = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.Session,
            SessionLifetime = TimeSpan.FromSeconds(30),
        });

        peerB.StartListener(0, "127.0.0.1");

        Assert.Null(peerA.GetActiveConnection(peerB.Listener!));

        await SendAndWaitForResponse(peerA, peerB.Listener!, "hello"u8.ToArray());

        IMsmtConnection connection = peerA.GetActiveConnection(peerB.Listener!)!;
        Assert.NotNull(connection.Sender);
        Assert.Equal(peerB.Listener!.Host, connection.Target.Host);
        Assert.Equal(peerB.Listener!.Port, connection.Target.Port);
        Assert.Null(connection.Receiver);
        Assert.Equal(connection.Sender!.Identity, connection.Identity);

        Assert.Null(peerA.GetActiveConnection(new MsmtTarget { Host = "127.0.0.1", Port = peerB.Listener!.Port + 1 }));
        Assert.Null(peerA.GetActiveConnection(new MsmtTarget { Host = "10.0.0.1", Port = peerB.Listener!.Port }));
    }

    /// <summary>
    /// <see cref="MsmtPeer.GetActiveConnection"/> also finds an incoming connection accepted by this peer's
    /// listener - not only an outgoing one this peer created via <see cref="MsmtPeer.Send"/> - when queried
    /// by that incoming connection's own remote endpoint, surfacing it through <see
    /// cref="IMsmtConnection.Receiver"/> rather than <see cref="IMsmtConnection.Sender"/>.
    /// </summary>
    [Fact]
    public async Task GetActiveConnection_IncomingConnectionAccepted_MatchesItByItsRemoteEndPointAsReceiver()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities },
            RequireFullyQualifiedHostname = false,
            Mode = MsmtOperationMode.Session,
            SessionLifetime = TimeSpan.FromSeconds(30),
        });
        await using MsmtPeer peerA = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });

        peerA.StartListener(0, "127.0.0.1");

        await SendAndWaitForResponse(peerB, peerA.Listener!, "hello"u8.ToArray());

        IMsmtConnection connection = Assert.Single(peerA.ActiveConnections);
        Assert.NotNull(connection.Receiver);
        Assert.Equal(MsmtLinkType.Receiver, connection.Receiver!.Kind);
        Assert.Null(connection.Sender);
        Assert.Equal(connection.Receiver.Identity, connection.Identity);

        Assert.Same(connection, peerA.GetActiveConnection(connection.Target));
    }

    /// <summary><see cref="IMsmtConnection.Drop"/> closes the connection's sender and removes it from its owning peer's on-demand connection cache.</summary>
    [Fact]
    public async Task Drop_CachedConnectionExists_RemovesItFromCache()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtPeer peerA = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.Session,
            SessionLifetime = TimeSpan.FromSeconds(30),
        });

        peerB.StartListener(0, "127.0.0.1");

        TaskCompletionSource<MsmtDisconnectedEventArgs> disconnected = new();
        peerA.Disconnected.Subscribe(args => disconnected.TrySetResult(args));

        await SendAndWaitForResponse(peerA, peerB.Listener!, "hello"u8.ToArray());
        IMsmtConnection connection = peerA.GetActiveConnection(peerB.Listener!)!;

        connection.Drop();

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(peerA.GetActiveConnection(peerB.Listener!));
    }

    /// <summary>
    /// <see cref="IMsmtConnection.Disconnect"/> gracefully closes the connection's sender, awaiting its
    /// teardown, removes it from its owning peer's on-demand connection cache, and - since this connection
    /// has no other link - raises the peer's connection-level <see cref="IMsmtPeer.Disconnected"/>.
    /// </summary>
    [Fact]
    public async Task Disconnect_CachedConnectionExists_AwaitsTeardownAndRemovesItFromCache()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtPeer peerA = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.Session,
            SessionLifetime = TimeSpan.FromSeconds(30),
        });

        peerB.StartListener(0, "127.0.0.1");

        bool disconnectedRaised = false;
        peerA.Disconnected.Subscribe(_ => disconnectedRaised = true);

        await SendAndWaitForResponse(peerA, peerB.Listener!, "hello"u8.ToArray());
        IMsmtConnection connection = peerA.GetActiveConnection(peerB.Listener!)!;

        await connection.Disconnect();

        Assert.True(disconnectedRaised);
        Assert.Null(peerA.GetActiveConnection(peerB.Listener!));
    }

    /// <summary>
    /// In Message Mode, a connection's sole link (its sender) closes after every message, so the peer's
    /// connection-level <see cref="IMsmtPeer.Connected"/> and <see cref="IMsmtPeer.Disconnected"/> each fire
    /// once per message - unlike the per-link <see cref="IMsmtPeer.Linked"/>/<see cref="IMsmtPeer.Unlinked"/>,
    /// which they mirror here since this connection never holds more than one link at a time.
    /// </summary>
    [Fact]
    public async Task ConnectedAndDisconnected_MessageModeMultipleSends_FireOncePerMessage()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtPeer peerA = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities } });

        peerB.StartListener(0, "127.0.0.1");

        int connectedCount = 0;
        int disconnectedCount = 0;
        TaskCompletionSource thirdDisconnect = new();
        peerA.Connected.Subscribe(args =>
        {
            connectedCount++;
            Assert.Equal(peerA.GetActiveConnection(peerB.Listener!), args.Connection);
        });
        peerA.Disconnected.Subscribe(_ =>
        {
            if (Interlocked.Increment(ref disconnectedCount) >= 3)
            {
                thirdDisconnect.TrySetResult();
            }
        });

        for (int i = 0; i < 3; i++)
        {
            await SendAndWaitForResponse(peerA, peerB.Listener!, "hello"u8.ToArray());
        }

        // Message Mode closes the sender only after the response has already been raised, so the third
        // send's own disconnect can still be pending when the loop above returns.
        await thirdDisconnect.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, connectedCount);
        Assert.Equal(3, disconnectedCount);
    }

    /// <summary><see cref="IMsmtLink.Drop"/> closes just that link, without going through its owning <see cref="IMsmtConnection"/>.</summary>
    [Fact]
    public async Task Drop_OnLinkDirectly_ClosesJustThatLink()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtPeer peerA = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.Session,
            SessionLifetime = TimeSpan.FromSeconds(30),
        });

        peerB.StartListener(0, "127.0.0.1");

        TaskCompletionSource<MsmtDisconnectedEventArgs> disconnected = new();
        peerA.Disconnected.Subscribe(args => disconnected.TrySetResult(args));

        await SendAndWaitForResponse(peerA, peerB.Listener!, "hello"u8.ToArray());
        IMsmtConnection connection = peerA.GetActiveConnection(peerB.Listener!)!;

        connection.Sender!.Drop();

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(peerA.GetActiveConnection(peerB.Listener!));
    }

    /// <summary><see cref="MsmtPeer.Dispose"/> immediately abandons the peer's listener and on-demand connections without waiting for either to finish.</summary>
    [Fact]
    public async Task Dispose_ImmediatelyAbandonsListenerAndConnections()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        MsmtPeer peerA = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.Session,
            SessionLifetime = TimeSpan.FromSeconds(30),
        });

        peerB.StartListener(0, "127.0.0.1");

        await SendAndWaitForResponse(peerA, peerB.Listener!, "hello"u8.ToArray());

        peerA.Dispose();
        peerB.Dispose();
    }

    /// <summary>Two sends to the same host and port reuse the same cached on-demand connection.</summary>
    [Fact]
    public async Task Send_SameHostAndPortTwice_ReusesCachedConnection()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtPeer peerA = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.Session,
            SessionLifetime = TimeSpan.FromSeconds(30),
        });

        peerB.StartListener(0, "127.0.0.1");

        List<IMsmtConnection> connected = [];
        TaskCompletionSource secondReceived = new();
        int receivedCount = 0;
        peerA.Connected.Subscribe(args => connected.Add(args.Connection));
        peerB.Received.Subscribe(_ =>
        {
            if (Interlocked.Increment(ref receivedCount) >= 2)
            {
                secondReceived.TrySetResult();
            }
        });

        MsmtTarget listener = peerB.Listener!;
        peerA.Send(listener, "one"u8.ToArray());
        peerA.Send(listener, "two"u8.ToArray());

        await secondReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(connected);
    }

    /// <summary>An on-demand connection idle for longer than <see cref="MsmtOptions.MaxIdleTime"/> is automatically disconnected in the background.</summary>
    [Fact]
    public async Task Send_ConnectionIdleLongerThanMaxIdleTime_AutomaticallyDisconnects()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtPeer peerA = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.Session,
            SessionLifetime = TimeSpan.FromSeconds(30),
            MaxIdleTime = TimeSpan.FromMilliseconds(200),
        });

        peerB.StartListener(0, "127.0.0.1");

        TaskCompletionSource<MsmtDisconnectedEventArgs> disconnected = new();
        peerA.Disconnected.Subscribe(args => disconnected.TrySetResult(args));

        peerA.Send(peerB.Listener!, "hello"u8.ToArray());

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// A send that refreshes activity while an earlier idle check for the same connection is already
    /// in flight postpones the automatic disconnect - the stale check reschedules itself for the correct
    /// remaining time instead of closing a connection that has since become active again. Measured against
    /// timestamps rather than fixed delays, since the first send's real TLS handshake latency is variable.
    /// </summary>
    [Fact]
    public async Task Send_RefreshesActivityWhileIdleCheckInFlight_PostponesAutomaticDisconnect()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        TimeSpan maxIdleTime = TimeSpan.FromMilliseconds(400);
        TimeSpan gapBetweenSends = TimeSpan.FromMilliseconds(200);

        await using MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtPeer peerA = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.MessageWithRekeying,
            RekeyLimit = 100,
            MaxIdleTime = maxIdleTime,
        });

        peerB.StartListener(0, "127.0.0.1");

        Stopwatch stopwatch = new();
        TaskCompletionSource<long> disconnectedAtMs = new();
        peerA.Disconnected.Subscribe(_ => disconnectedAtMs.TrySetResult(stopwatch.ElapsedMilliseconds));

        // The first send's own TLS handshake latency is unpredictable, so timing is measured from the
        // second send onward rather than assumed from a fixed delay after the first.
        await SendAndWaitForResponse(peerA, peerB.Listener!, "one"u8.ToArray());
        await Task.Delay(gapBetweenSends);
        stopwatch.Start();
        await SendAndWaitForResponse(peerA, peerB.Listener!, "two"u8.ToArray());

        long elapsedAtDisconnectMs = await disconnectedAtMs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Had the stale check (scheduled from the first send) closed the connection without rechecking
        // activity, it would have fired around maxIdleTime after the first send - i.e. close to
        // maxIdleTime - gapBetweenSends after the second one, well under half of maxIdleTime here.
        // Closing near a full maxIdleTime after the second send instead proves it rescheduled.
        Assert.True(elapsedAtDisconnectMs >= maxIdleTime.TotalMilliseconds * 0.75, $"Disconnected only {elapsedAtDisconnectMs}ms after the second send, well short of maxIdleTime ({maxIdleTime.TotalMilliseconds}ms) - the stale idle check likely did not reschedule.");
    }

    /// <summary>Exceeding <see cref="MsmtOptions.MaxConnectionCount"/> automatically disconnects the connection with the oldest traffic.</summary>
    [Fact]
    public async Task Send_ExceedsMaxConnectionCount_DisconnectsOldestConnection()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        // peerA is declared (and therefore disposed) last, since some of its outgoing Session Mode
        // connections may still be open when the test ends and must close before the servers that
        // accepted them try to await their accepted connections' teardown.
        await using MsmtServer serverOne = new();
        await using MsmtServer serverTwo = new();
        await using MsmtServer serverThree = new();
        await using MsmtPeer peerA = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.Session,
            SessionLifetime = TimeSpan.FromSeconds(30),
            MaxConnectionCount = 2,
        });

        serverOne.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities },
        });
        serverTwo.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities },
        });
        serverThree.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities },
        });

        ConcurrentDictionary<IPEndPoint, TaskCompletionSource> received = new();
        MsmtServer[] servers = [serverOne, serverTwo, serverThree];
        foreach (MsmtServer server in servers)
        {
            TaskCompletionSource source = new();
            received[server.LocalEndPoint!] = source;
            server.Received += (_, _) => source.TrySetResult();
        }

        TaskCompletionSource<MsmtDisconnectedEventArgs> disconnected = new();
        peerA.Disconnected.Subscribe(args => disconnected.TrySetResult(args));

        peerA.Send(new MsmtTarget { Host = serverOne.LocalEndPoint!.Address.ToString(), Port = serverOne.LocalEndPoint!.Port }, "one"u8.ToArray());
        await received[serverOne.LocalEndPoint!].Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Task.Delay(TimeSpan.FromMilliseconds(20));

        peerA.Send(new MsmtTarget { Host = serverTwo.LocalEndPoint!.Address.ToString(), Port = serverTwo.LocalEndPoint!.Port }, "two"u8.ToArray());
        await received[serverTwo.LocalEndPoint!].Task.WaitAsync(TimeSpan.FromSeconds(5));

        peerA.Send(new MsmtTarget { Host = serverThree.LocalEndPoint!.Address.ToString(), Port = serverThree.LocalEndPoint!.Port }, "three"u8.ToArray());
        await received[serverThree.LocalEndPoint!].Task.WaitAsync(TimeSpan.FromSeconds(5));

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>With <see cref="MsmtOptions.MaxIdleTime"/> set to <see langword="null"/>, an on-demand connection is never automatically disconnected for being idle.</summary>
    [Fact]
    public async Task Send_MaxIdleTimeNull_NeverAutomaticallyDisconnectsForIdleness()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtPeer peerA = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.Session,
            SessionLifetime = TimeSpan.FromSeconds(30),
            MaxIdleTime = null,
        });

        peerB.StartListener(0, "127.0.0.1");

        TaskCompletionSource<MsmtDisconnectedEventArgs> disconnected = new();
        peerA.Disconnected.Subscribe(args => disconnected.TrySetResult(args));

        peerA.Send(peerB.Listener!, "hello"u8.ToArray());

        // Waiting well past a typical idle threshold with no MaxIdleTime configured confirms the idle rule
        // truly never fires, rather than merely not having fired yet.
        await Task.WhenAny(disconnected.Task, Task.Delay(TimeSpan.FromSeconds(2.5)));

        Assert.False(disconnected.Task.IsCompleted);
        Assert.Single(peerA.ActiveConnections);
    }

    /// <summary>With <see cref="MsmtOptions.MaxConnectionCount"/> set to <see langword="null"/>, on-demand connections are never automatically disconnected for exceeding a connection count limit.</summary>
    [Fact]
    public async Task Send_MaxConnectionCountNull_NeverAutomaticallyDisconnectsForExceedingCount()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer serverOne = new();
        await using MsmtServer serverTwo = new();
        await using MsmtServer serverThree = new();
        await using MsmtPeer peerA = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.Session,
            SessionLifetime = TimeSpan.FromSeconds(30),
            MaxConnectionCount = null,
        });

        serverOne.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities },
        });
        serverTwo.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities },
        });
        serverThree.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities },
        });

        ConcurrentDictionary<IPEndPoint, TaskCompletionSource> received = new();
        MsmtServer[] servers = [serverOne, serverTwo, serverThree];
        foreach (MsmtServer server in servers)
        {
            TaskCompletionSource source = new();
            received[server.LocalEndPoint!] = source;
            server.Received += (_, _) => source.TrySetResult();
        }

        TaskCompletionSource<MsmtDisconnectedEventArgs> disconnected = new();
        peerA.Disconnected.Subscribe(args => disconnected.TrySetResult(args));

        peerA.Send(new MsmtTarget { Host = serverOne.LocalEndPoint!.Address.ToString(), Port = serverOne.LocalEndPoint!.Port }, "one"u8.ToArray());
        await received[serverOne.LocalEndPoint!].Task.WaitAsync(TimeSpan.FromSeconds(5));

        peerA.Send(new MsmtTarget { Host = serverTwo.LocalEndPoint!.Address.ToString(), Port = serverTwo.LocalEndPoint!.Port }, "two"u8.ToArray());
        await received[serverTwo.LocalEndPoint!].Task.WaitAsync(TimeSpan.FromSeconds(5));

        peerA.Send(new MsmtTarget { Host = serverThree.LocalEndPoint!.Address.ToString(), Port = serverThree.LocalEndPoint!.Port }, "three"u8.ToArray());
        await received[serverThree.LocalEndPoint!].Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The eviction loop checks every second; waiting through several cycles with three connections
        // open confirms the count rule truly never fires, rather than merely not having fired yet.
        await Task.WhenAny(disconnected.Task, Task.Delay(TimeSpan.FromSeconds(2.5)));

        Assert.False(disconnected.Task.IsCompleted);
        Assert.Equal(3, peerA.ActiveConnections.Count);
    }

    /// <summary>
    /// An accepted connection idle for longer than the listening peer's own <see
    /// cref="MsmtOptions.MaxIdleTime"/> is automatically disconnected, symmetric with <see
    /// cref="Send_ConnectionIdleLongerThanMaxIdleTime_AutomaticallyDisconnects"/> for the sending side. Uses
    /// <see cref="MsmtOperationMode.MessageWithRekeying"/> so the connection stays open between messages -
    /// this rule has no effect on <see cref="MsmtOperationMode.Message"/>, which the client already closes
    /// immediately after each message.
    /// </summary>
    [Fact]
    public async Task Received_AcceptedConnectionIdleLongerThanMaxIdleTime_AutomaticallyDisconnects()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities },
            RequireFullyQualifiedHostname = false,
            MaxIdleTime = TimeSpan.FromMilliseconds(200),
        });
        await using MsmtPeer peerA = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.MessageWithRekeying,
            RekeyLimit = 100,
        });

        peerB.StartListener(0, "127.0.0.1");

        TaskCompletionSource<MsmtDisconnectedEventArgs> disconnectedOnB = new();
        peerB.Disconnected.Subscribe(args => disconnectedOnB.TrySetResult(args));

        await SendAndWaitForResponse(peerA, peerB.Listener!, "hello"u8.ToArray());

        await disconnectedOnB.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// <see cref="MsmtOptions.MaxIdleTime"/> never disconnects a connection while it is actively processing
    /// a message - only the gap between message cycles counts as idle - even if that processing takes
    /// longer than the configured idle threshold.
    /// </summary>
    [Fact]
    public async Task Received_SlowSubscriberLongerThanMaxIdleTime_DoesNotDisconnectMidCycle()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities },
            RequireFullyQualifiedHostname = false,
            MaxIdleTime = TimeSpan.FromMilliseconds(50),
        });
        await using MsmtPeer peerA = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.MessageWithRekeying,
            RekeyLimit = 100,
        });

        peerB.Received.Subscribe(args =>
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(500));
            args.Payload.Dispose();
            args.Responder.Accept();
        });

        peerB.StartListener(0, "127.0.0.1");

        // MessageWithRekeying keeps the connection open past this one exchange, so any Unlinked here would
        // have to be caused by the idle timeout rather than the client's own (Message Mode only) teardown.
        bool timedOutMidCycle = false;
        peerB.Unlinked.Subscribe(args => timedOutMidCycle |= args.Exception is TimeoutException);

        MsmtResponse response = await peerA.Request(peerB.Listener!, "hello"u8.ToArray()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(response.Success);
        Assert.False(timedOutMidCycle);
    }

    /// <summary>
    /// Exceeding the listening peer's own <see cref="MsmtOptions.MaxConnectionCount"/> automatically
    /// disconnects the accepted connection with the oldest activity, symmetric with <see
    /// cref="Send_ExceedsMaxConnectionCount_DisconnectsOldestConnection"/> for the sending side.
    /// </summary>
    [Fact]
    public async Task Received_ExceedsMaxConnectionCount_DisconnectsOldestAcceptedConnection()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities },
            RequireFullyQualifiedHostname = false,
            MaxConnectionCount = 2,
        });

        // Each client peer is declared (and therefore disposed) before peerB, since their outgoing
        // MessageWithRekeying connections may still be open when the test ends and must close before
        // peerB tries to await its accepted connections' teardown.
        await using MsmtPeer clientOne = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities }, Mode = MsmtOperationMode.MessageWithRekeying, RekeyLimit = 100 });
        await using MsmtPeer clientTwo = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities }, Mode = MsmtOperationMode.MessageWithRekeying, RekeyLimit = 100 });
        await using MsmtPeer clientThree = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities }, Mode = MsmtOperationMode.MessageWithRekeying, RekeyLimit = 100 });

        peerB.StartListener(0, "127.0.0.1");

        TaskCompletionSource<MsmtDisconnectedEventArgs> disconnectedOnB = new();
        peerB.Disconnected.Subscribe(args => disconnectedOnB.TrySetResult(args));

        await SendAndWaitForResponse(clientOne, peerB.Listener!, "one"u8.ToArray());
        await Task.Delay(TimeSpan.FromMilliseconds(20));

        await SendAndWaitForResponse(clientTwo, peerB.Listener!, "two"u8.ToArray());
        await SendAndWaitForResponse(clientThree, peerB.Listener!, "three"u8.ToArray());

        await disconnectedOnB.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// A <see cref="MsmtOptions.MaxIdleTime"/> longer than a single <see
    /// cref="CancellationTokenSource"/>/<see cref="Task.Delay(TimeSpan)"/> timer can represent (roughly 49.7
    /// days) does not crash an accepted connection's idle-timeout enforcement - it must be chained across
    /// several clamped waits rather than attempted as one, on both the sending and receiving sides.
    /// </summary>
    [Fact]
    public async Task Received_MaxIdleTimeBeyondSingleTimerLimit_DoesNotCrashAcceptedConnection()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities },
            RequireFullyQualifiedHostname = false,
            MaxIdleTime = TimeSpan.FromDays(60),
        });
        await using MsmtPeer peerA = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities },
            MaxIdleTime = TimeSpan.FromDays(60),
        });

        peerB.StartListener(0, "127.0.0.1");

        MsmtResponse response = await peerA.Request(peerB.Listener!, "hello"u8.ToArray()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(response.Success);
    }

    /// <summary>
    /// <see cref="TimeSpan.MaxValue"/> as <see cref="MsmtOptions.MaxIdleTime"/> does not crash an accepted
    /// connection's idle-timeout enforcement - tracking remaining idle time as a <see cref="TimeSpan"/>
    /// avoids the <see cref="DateTime"/> arithmetic overflow an absolute deadline computed via
    /// <c>DateTime.UtcNow + MaxIdleTime</c> would hit for a duration this large.
    /// </summary>
    [Fact]
    public async Task Received_MaxIdleTimeSpanMaxValue_DoesNotCrashAcceptedConnection()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities },
            RequireFullyQualifiedHostname = false,
            MaxIdleTime = TimeSpan.MaxValue,
        });
        await using MsmtPeer peerA = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities },
            MaxIdleTime = TimeSpan.MaxValue,
        });

        peerB.StartListener(0, "127.0.0.1");

        MsmtResponse response = await peerA.Request(peerB.Listener!, "hello"u8.ToArray()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(response.Success);
    }

    /// <summary>
    /// A payload larger than <see cref="MsmtLimits.MaxPayloadLength"/> is rejected up front by <see
    /// cref="IMsmtPeer.Send"/>/<see cref="IMsmtPeer.Request"/> rather than being transmitted with a header
    /// the remote peer must reject as malformed - which would surface as an opaque transport error and tear
    /// the connection down without ever delivering the message.
    /// </summary>
    [Fact]
    public async Task SendAndRequest_PayloadLongerThanMaxPayloadLength_ThrowsWithoutTransmitting()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtPeer peerA = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities } });

        peerB.StartListener(0, "127.0.0.1");

        byte[] oversized = new byte[MsmtLimits.MaxPayloadLength + 1];

        Assert.Throws<ArgumentOutOfRangeException>(() => peerA.Send(peerB.Listener!, oversized));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => peerA.Request(peerB.Listener!, oversized));

        // Rejected before any connection was opened for it, so nothing was transmitted.
        Assert.Empty(peerA.ActiveConnections);

        // A payload exactly at the limit is still accepted.
        MsmtResponse response = await peerA.Request(peerB.Listener!, new byte[MsmtLimits.MaxPayloadLength]).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(response.Success);
        response.Payload.Dispose();
    }

    /// <summary>
    /// An acknowledgement payload larger than <see cref="MsmtLimits.MaxPayloadLength"/> is rejected by <see
    /// cref="IMsmtResponder"/> rather than written as a malformed acknowledgement the sender cannot match to
    /// its request.
    /// </summary>
    [Fact]
    public async Task Accept_PayloadLongerThanMaxPayloadLength_Throws()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtPeer peerA = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities } });

        TaskCompletionSource<Exception?> rejected = new();
        peerB.Received.Subscribe(args =>
        {
            using (args.Payload)
            {
                rejected.TrySetResult(Record.Exception(() => args.Responder.Accept(new byte[MsmtLimits.MaxPayloadLength + 1])));
                args.Responder.Accept();
            }
        });

        peerB.StartListener(0, "127.0.0.1");

        await peerA.Request(peerB.Listener!, "hello"u8.ToArray()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<ArgumentOutOfRangeException>(await rejected.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>Before <see cref="MsmtPeer.StartListener"/> is called, the peer is not listening and has no local endpoint.</summary>
    [Fact]
    public async Task IsListening_BeforeStartListener_IsFalse()
    {
        (X509Certificate2 certificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peer = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificate, TrustedAuthorities = trustedAuthorities } });

        Assert.False(peer.IsListening);
        Assert.Null(peer.Listener);
    }

    /// <summary><see cref="MsmtPeer.StopListener"/> does nothing if the peer was never listening.</summary>
    [Fact]
    public async Task StopListener_NeverStarted_DoesNothing()
    {
        (X509Certificate2 certificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peer = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificate, TrustedAuthorities = trustedAuthorities } });

        peer.StopListener();

        Assert.False(peer.IsListening);
    }

    /// <summary><see cref="MsmtPeer.StartListener"/> transitions the peer to listening with a resolved local endpoint; <see cref="MsmtPeer.StopListener"/> transitions it back.</summary>
    [Fact]
    public async Task StartListener_ThenStopListener_TogglesIsListening()
    {
        (X509Certificate2 certificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peer = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificate, TrustedAuthorities = trustedAuthorities } });

        peer.StartListener(0, "127.0.0.1");

        Assert.True(peer.IsListening);
        Assert.NotNull(peer.Listener);

        peer.StopListener();

        Assert.False(peer.IsListening);
        Assert.Null(peer.Listener);
    }

    /// <summary>Calling <see cref="MsmtPeer.StartListener"/> again while already listening stops the previous listener first, releasing its port rather than leaking its listener and accept loop.</summary>
    [Fact]
    public async Task StartListener_CalledAgainWhileListening_StopsPreviousListenerFirst()
    {
        (X509Certificate2 certificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peer = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificate, TrustedAuthorities = trustedAuthorities } });

        peer.StartListener(0, "127.0.0.1");
        MsmtTarget firstListener = peer.Listener!;

        peer.StartListener(0, "127.0.0.1");
        MsmtTarget secondListener = peer.Listener!;

        Assert.NotEqual(firstListener.Port, secondListener.Port);

        using TcpClient client = new();
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(firstListener.Host, firstListener.Port));
    }

    /// <summary><see cref="MsmtPeer.GetPackage"/> returns <see langword="null"/> for a tag no send was ever queued with.</summary>
    [Fact]
    public async Task GetPackage_UnknownTag_ReturnsNull()
    {
        (X509Certificate2 certificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peer = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificate, TrustedAuthorities = trustedAuthorities } });
        object tag = new();

        Assert.Null(peer.GetPackage(tag));
    }

    /// <summary>Once a tagged send to another peer completes, <see cref="MsmtPeer.GetPackage"/> reflects its final status and it is no longer present in <see cref="MsmtPeer.Packages"/>.</summary>
    [Fact]
    public async Task Send_TaggedPayload_GetPackageReflectsCompletion()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtPeer peerA = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities } });

        peerB.StartListener(0, "127.0.0.1");

        object tag = new();
        TaskCompletionSource completed = new();
        peerA.PackageChanged.Subscribe(args =>
        {
            if (ReferenceEquals(args.Package.Tag, tag) && args.Status == MsmtSendStatus.Completed)
            {
                completed.TrySetResult();
            }
        });

        MsmtNameTarget target = peerB.Listener!;
        peerA.Send(target, "hello"u8.ToArray(), new MsmtSendOptions { Tag = tag });
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        IMsmtPackage package = peerA.GetPackage(tag)!;
        Assert.Equal(MsmtSendStatus.Completed, package.Status);
        Assert.Equal(target, package.Target);
        Assert.DoesNotContain(peerA.Packages, other => ReferenceEquals(other.Tag, tag));
    }

    /// <summary>
    /// Sending to a remote peer whose certificate is signed by an authority this peer does not trust raises
    /// <see cref="IMsmtPeer.LinkFailed"/> (not <see cref="IMsmtPeer.Linked"/>), and the send's own <see
    /// cref="IMsmtPeer.Request"/> task faults with the same underlying cause.
    /// </summary>
    [Fact]
    public async Task Request_ServerCertificateUntrusted_RaisesLinkFailedAndFaultsRequest()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        (_, _, X509Certificate2Collection unrelatedTrustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtPeer peerA = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = unrelatedTrustedAuthorities } });

        peerB.StartListener(0, "127.0.0.1");

        TaskCompletionSource<MsmtLinkFailedEventArgs> linkFailed = new();
        peerA.LinkFailed.Subscribe(args => linkFailed.TrySetResult(args));

        await Assert.ThrowsAnyAsync<Exception>(() => peerA.Request(peerB.Listener!, "hello"u8.ToArray()));

        MsmtLinkFailedEventArgs failure = await linkFailed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MsmtLinkType.Sender, failure.Link.Kind);
        Assert.NotNull(failure.Exception);
    }

    /// <summary>
    /// A client presenting a certificate signed by an authority the listening peer does not trust fails the
    /// handshake, raising <see cref="IMsmtPeer.LinkFailed"/> for the receiving direction rather than <see
    /// cref="IMsmtPeer.Linked"/>.
    /// </summary>
    [Fact]
    public async Task Received_ClientCertificateUntrusted_RaisesLinkFailedForReceivingDirection()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        (_, _, X509Certificate2Collection unrelatedTrustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = unrelatedTrustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtPeer peerA = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities } });

        peerB.StartListener(0, "127.0.0.1");

        TaskCompletionSource<MsmtLinkFailedEventArgs> linkFailedOnB = new();
        peerB.LinkFailed.Subscribe(args => linkFailedOnB.TrySetResult(args));

        Record.Exception(() => peerA.Send(peerB.Listener!, "hello"u8.ToArray()));

        MsmtLinkFailedEventArgs failure = await linkFailedOnB.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MsmtLinkType.Receiver, failure.Link.Kind);
        Assert.NotNull(failure.Exception);
    }

    /// <summary><see cref="IMsmtPeer.Test"/> returns <see langword="true"/> when the target peer is reachable and correctly configured, without ever raising <see cref="IMsmtPeer.Received"/>.</summary>
    [Fact]
    public async Task Test_TargetReachable_ReturnsTrueWithoutRaisingReceived()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtPeer peerA = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities } });

        bool receivedRaised = false;
        peerB.Received.Subscribe(_ => receivedRaised = true);
        peerB.StartListener(0, "127.0.0.1");

        bool reachable = await peerA.Test(peerB.Listener!).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(reachable);
        Assert.False(receivedRaised);
    }

    /// <summary><see cref="IMsmtPeer.Test"/> against a target with nothing listening throws rather than returning <see langword="false"/>, since the connection itself never came up.</summary>
    [Fact]
    public async Task Test_NothingListening_Throws()
    {
        (X509Certificate2 certificateA, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtPeer peerA = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities } });

        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int unusedPort = ((IPEndPoint)probe.LocalEndPoint!).Port;

        await Assert.ThrowsAnyAsync<Exception>(() => peerA.Test(new MsmtTarget { Host = "127.0.0.1", Port = unusedPort }));
    }

    /// <summary>
    /// Two peers can each act as both client and server at once: peer A sending to peer B and peer B sending
    /// to peer A are entirely independent exchanges, each fully delivered and acknowledged.
    /// </summary>
    [Fact]
    public async Task Request_BidirectionalBetweenTwoPeers_BothDirectionsSucceed()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtPeer peerA = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });

        TaskCompletionSource<string> receivedByA = new();
        TaskCompletionSource<string> receivedByB = new();
        peerA.Received.Subscribe(args =>
        {
            using (args.Payload)
            {
                receivedByA.TrySetResult(Encoding.UTF8.GetString(args.Payload.Memory.Span));
            }
        });
        peerB.Received.Subscribe(args =>
        {
            using (args.Payload)
            {
                receivedByB.TrySetResult(Encoding.UTF8.GetString(args.Payload.Memory.Span));
            }
        });

        peerA.StartListener(0, "127.0.0.1");
        peerB.StartListener(0, "127.0.0.1");

        MsmtResponse responseFromB = await SendAndWaitForResponse(peerA, peerB.Listener!, "from A"u8.ToArray());
        MsmtResponse responseFromA = await SendAndWaitForResponse(peerB, peerA.Listener!, "from B"u8.ToArray());

        Assert.True(responseFromB.Success);
        Assert.True(responseFromA.Success);
        Assert.Equal("from A", await receivedByB.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("from B", await receivedByA.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        responseFromB.Payload.Dispose();
        responseFromA.Payload.Dispose();
    }

    /// <summary>
    /// A peer resolved through dependency injection (<see cref="MsmtServiceCollectionExtensions.AddMsmt"/>)
    /// behaves identically to one constructed directly: a full send/request-response exchange succeeds.
    /// </summary>
    [Fact]
    public async Task Request_PeerResolvedViaDependencyInjection_DeliversMessageAndAcknowledgement()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        ServiceCollection services = new();
        services.AddMsmt();
        using ServiceProvider provider = services.BuildServiceProvider();
        IMsmtPeerFactory factory = provider.GetRequiredService<IMsmtPeerFactory>();

        await using IMsmtPeer peerB = factory.Create(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using IMsmtPeer peerA = factory.Create(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities } });

        TaskCompletionSource<MsmtReceivedEventArgs> receivedByB = new();
        peerB.Received.Subscribe(args => receivedByB.TrySetResult(args));
        peerB.StartListener(0, "127.0.0.1");

        MsmtResponse response = await SendAndWaitForResponse(peerA, peerB.Listener!, "hello"u8.ToArray());

        Assert.True(response.Success);
        response.Payload.Dispose();
        MsmtReceivedEventArgs received = await receivedByB.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hello"u8.ToArray(), received.Payload.Memory.ToArray());
    }

    /// <summary>
    /// Calling <see cref="IMsmtLink.Drop"/> directly on an accepted (<see cref="IMsmtConnection.Receiver"/>)
    /// link immediately closes just that link, without going through <see cref="IMsmtConnection.Drop"/>.
    /// </summary>
    [Fact]
    public async Task Drop_OnAcceptedReceiverLinkDirectly_ClosesJustThatLink()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtPeer peerB = new(new MsmtOptions { Credentials = new MsmtCredentials { Identity = certificateB, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });
        await using MsmtPeer peerA = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.MessageWithRekeying,
            RekeyLimit = 100,
        });

        peerB.StartListener(0, "127.0.0.1");

        TaskCompletionSource<MsmtDisconnectedEventArgs> disconnectedOnB = new();
        peerB.Disconnected.Subscribe(args => disconnectedOnB.TrySetResult(args));

        await SendAndWaitForResponse(peerA, peerB.Listener!, "hello"u8.ToArray());
        IMsmtConnection connectionOnB = peerB.ActiveConnections.Single();

        connectionOnB.Receiver!.Drop();

        await disconnectedOnB.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(peerB.ActiveConnections);
    }

    private static async Task<MsmtResponse> SendAndWaitForResponse(IMsmtPeer peer, MsmtNameTarget target, ReadOnlyMemory<byte> payload) =>
        await peer.Request(target, payload).WaitAsync(TimeSpan.FromSeconds(5));
}
