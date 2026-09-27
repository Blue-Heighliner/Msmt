namespace BlueHeighliner.Msmt.Tests.Integration;

/// <summary>Integration tests for bidirectional session connections between two <see cref="MsmtSessionPeer"/> instances.</summary>
public sealed class MsmtSessionTests
{
    private readonly TimeSpan waitLimit = TimeSpan.FromSeconds(10);

    /// <summary>Connecting completes the handshake and session negotiation, and both sides report the connection along with the other side's verified identity.</summary>
    [Fact]
    public async Task Connect_ValidListener_BothSidesRaiseConnectedWithIdentities()
    {
        (X509Certificate2 connectingCertificate, X509Certificate2 listeningCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtSessionPeer listening = new(Options(listeningCertificate, trustedAuthorities));
        await using MsmtSessionPeer connecting = new(Options(connectingCertificate, trustedAuthorities));
        Task<IMsmtConnection> listeningSide = WaitFor(listening.Connected);
        listening.StartListener(0, "127.0.0.1");

        IMsmtConnection connection = connecting.Connect(Target(listening));

        Assert.True(await connection.Wait().WaitAsync(waitLimit));
        IMsmtConnection accepted = await listeningSide;
        Assert.Equal(MsmtConnectionStatus.Connected, connection.Status);
        Assert.Equal(MsmtConnectionStatus.Connected, accepted.Status);
        Assert.Equal(MsmtIdentity.FromCertificate(listeningCertificate), connection.Identity);
        Assert.Equal(MsmtIdentity.FromCertificate(connectingCertificate), accepted.Identity);
        Assert.True(connection.Expiration > DateTime.UtcNow);
        Assert.Single(listening.Connections);
        Assert.Single(connecting.Connections);
    }

    /// <summary>The connecting side can send to the listening side and be acknowledged, with the payload and response both intact.</summary>
    [Fact]
    public async Task Request_ConnectingToListening_ReceivesAcknowledgementWithPayload()
    {
        (IMsmtSessionPeer listening, IMsmtSessionPeer connecting, IMsmtConnection connectingSide, IMsmtConnection listeningSide) = await ConnectPair();
        await using (listening)
        await using (connecting)
        {
            TaskCompletionSource<(string Text, MsmtTarget Source)> received = new();
            listening.Receiver = (connection, payload, isResponseRequested) =>
            {
                received.TrySetResult((Encoding.UTF8.GetString(payload.Span), connection.Remote));
                return new ValueTask<MsmtReceiveResult?>(MsmtReceiveResult.Accept("thanks"u8.ToArray()));
            };

            MsmtResponse response = await connectingSide.Request("hello"u8.ToArray()).WaitAsync(waitLimit);

            Assert.True(response.Success);
            Assert.Equal("thanks", Encoding.UTF8.GetString(response.Payload.Memory.Span));
            (string text, MsmtTarget source) = await received.Task.WaitAsync(waitLimit);
            Assert.Equal("hello", text);
            Assert.Equal(listeningSide.Remote, source);
        }
    }

    /// <summary>The listening side can send to the connecting side over the connection the connecting side opened, which is what makes a session bidirectional.</summary>
    [Fact]
    public async Task Request_ListeningToConnecting_ReceivesAcknowledgement()
    {
        (IMsmtSessionPeer listening, IMsmtSessionPeer connecting, IMsmtConnection connectingSide, IMsmtConnection listeningSide) = await ConnectPair();
        await using (listening)
        await using (connecting)
        {
            connecting.Receiver = (connection, payload, isResponseRequested) =>
            {
                Assert.Equal("from listener", Encoding.UTF8.GetString(payload.Span));
                return new ValueTask<MsmtReceiveResult?>(MsmtReceiveResult.Accept("connecting side ack"u8.ToArray()));
            };

            MsmtResponse response = await listeningSide.Request("from listener"u8.ToArray()).WaitAsync(waitLimit);

            Assert.True(response.Success);
            Assert.Equal("connecting side ack", Encoding.UTF8.GetString(response.Payload.Memory.Span));
        }
    }

    /// <summary>Requests in both directions at once, each acknowledged by a slow subscriber, all complete without waiting for one another.</summary>
    [Fact]
    public async Task Request_BothDirectionsAtOnce_AllComplete()
    {
        (IMsmtSessionPeer listening, IMsmtSessionPeer connecting, IMsmtConnection connectingSide, IMsmtConnection listeningSide) = await ConnectPair();
        await using (listening)
        await using (connecting)
        {
            ValueTask<MsmtReceiveResult?> EchoSlowly(IMsmtConnection connection, ReadOnlyMemory<byte> payload, bool isResponseRequested)
            {
                Thread.Sleep(50);
                return new ValueTask<MsmtReceiveResult?>(MsmtReceiveResult.Accept(payload.ToArray()));
            }

            connecting.Receiver = EchoSlowly;
            listening.Receiver = EchoSlowly;

            List<Task<MsmtResponse>> requests = [];
            for (int index = 0; index < 20; index++)
            {
                requests.Add(connectingSide.Request(Encoding.ASCII.GetBytes($"c{index}")));
                requests.Add(listeningSide.Request(Encoding.ASCII.GetBytes($"l{index}")));
            }

            MsmtResponse[] responses = await Task.WhenAll(requests).WaitAsync(waitLimit);

            for (int index = 0; index < 20; index++)
            {
                Assert.Equal($"c{index}", Encoding.ASCII.GetString(responses[index * 2].Payload.Memory.Span));
                Assert.Equal($"l{index}", Encoding.ASCII.GetString(responses[(index * 2) + 1].Payload.Memory.Span));
            }
        }
    }

    /// <summary>A message the listening side sends as soon as it accepts a connection reaches the connecting side, which subscribed to receive before connecting.</summary>
    [Fact]
    public async Task Connected_ListenerSendsImmediately_ConnectingSideReceivesIt()
    {
        (X509Certificate2 connectingCertificate, X509Certificate2 listeningCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtSessionPeer listening = new(Options(listeningCertificate, trustedAuthorities));
        await using MsmtSessionPeer connecting = new(Options(connectingCertificate, trustedAuthorities));
        TaskCompletionSource<string> received = new();
        connecting.Receiver = (connection, payload, isResponseRequested) =>
        {
            received.TrySetResult(Encoding.UTF8.GetString(payload.Span));
            return default;
        };
        listening.Connected.Subscribe(connection => connection.Send("welcome"u8.ToArray()));
        listening.StartListener(0, "127.0.0.1");

        IMsmtConnection connection = connecting.Connect(Target(listening));
        Assert.True(await connection.Wait().WaitAsync(waitLimit));

        Assert.Equal("welcome", await received.Task.WaitAsync(waitLimit));
    }

    /// <summary>A rejection carries its own payload and reports failure to the sender, and a response the handler awaits before returning is delivered once decided, later.</summary>
    [Fact]
    public async Task Received_RejectImmediatelyAndDecideLater_ReachSender()
    {
        (IMsmtSessionPeer listening, IMsmtSessionPeer connecting, IMsmtConnection connectingSide, IMsmtConnection listeningSide) = await ConnectPair();
        await using (listening)
        await using (connecting)
        {
            TaskCompletionSource<MsmtReceiveResult> deferred = new();
            TaskCompletionSource deferredReady = new();
            listening.Receiver = async (connection, payload, isResponseRequested) =>
            {
                if (Encoding.ASCII.GetString(payload.Span) == "reject")
                {
                    return MsmtReceiveResult.Reject("no"u8.ToArray());
                }

                deferredReady.TrySetResult();
                return await deferred.Task;
            };

            MsmtResponse rejected = await connectingSide.Request("reject"u8.ToArray()).WaitAsync(waitLimit);
            Assert.False(rejected.Success);
            Assert.Equal("no", Encoding.ASCII.GetString(rejected.Payload.Memory.Span));

            Task<MsmtResponse> pending = connectingSide.Request("later"u8.ToArray());
            await deferredReady.Task.WaitAsync(waitLimit);
            Assert.False(pending.IsCompleted);

            deferred.TrySetResult(MsmtReceiveResult.Accept("done"u8.ToArray()));
            Assert.Equal("done", Encoding.ASCII.GetString((await pending.WaitAsync(waitLimit)).Payload.Memory.Span));
        }
    }

    /// <summary>A message with no subscriber to answer it is accepted automatically, so the sender is not left waiting.</summary>
    [Fact]
    public async Task Request_NoSubscriber_IsAcceptedAutomatically()
    {
        (IMsmtSessionPeer listening, IMsmtSessionPeer connecting, IMsmtConnection connectingSide, _) = await ConnectPair();
        await using (listening)
        await using (connecting)
        {
            Assert.True((await connectingSide.Request("anyone?"u8.ToArray()).WaitAsync(waitLimit)).Success);
            connectingSide.Send("plain send"u8.ToArray());
        }
    }

    /// <summary>A payload of several megabytes goes both ways intact.</summary>
    [Fact]
    public async Task Request_LargePayloadBothWays_ArrivesIntact()
    {
        (IMsmtSessionPeer listening, IMsmtSessionPeer connecting, IMsmtConnection connectingSide, IMsmtConnection listeningSide) = await ConnectPair();
        await using (listening)
        await using (connecting)
        {
            byte[] payload = new byte[4 * 1024 * 1024];
            new Random(3).NextBytes(payload);
            listening.Receiver = (connection, receivedPayload, isResponseRequested) =>
                new ValueTask<MsmtReceiveResult?>(MsmtReceiveResult.Accept(receivedPayload.ToArray()));

            MsmtResponse response = await connectingSide.Request(payload).WaitAsync(waitLimit);

            Assert.True(response.Payload.Memory.Span.SequenceEqual(payload));
        }
    }

    /// <summary>
    /// Several-megabyte payloads sent by both sides at the same time both arrive intact, over the one socket
    /// the connection uses in either direction - proof the channel reads and writes concurrently rather than
    /// only ever carrying traffic one way at a time.
    /// </summary>
    [Fact]
    public async Task Request_LargePayloadsBothDirectionsSimultaneously_ArriveIntactOverOneSocket()
    {
        (IMsmtSessionPeer listening, IMsmtSessionPeer connecting, IMsmtConnection connectingSide, IMsmtConnection listeningSide) = await ConnectPair();
        await using (listening)
        await using (connecting)
        {
            byte[] fromConnecting = new byte[4 * 1024 * 1024];
            byte[] fromListening = new byte[4 * 1024 * 1024];
            new Random(1).NextBytes(fromConnecting);
            new Random(2).NextBytes(fromListening);
            TaskCompletionSource<byte[]> receivedByListening = new();
            TaskCompletionSource<byte[]> receivedByConnecting = new();
            listening.Receiver = (connection, payload, isResponseRequested) =>
            {
                receivedByListening.TrySetResult(payload.ToArray());
                return new ValueTask<MsmtReceiveResult?>(MsmtReceiveResult.Accept());
            };
            connecting.Receiver = (connection, payload, isResponseRequested) =>
            {
                receivedByConnecting.TrySetResult(payload.ToArray());
                return new ValueTask<MsmtReceiveResult?>(MsmtReceiveResult.Accept());
            };

            Task<MsmtResponse> toListening = connectingSide.Request(fromConnecting);
            Task<MsmtResponse> toConnecting = listeningSide.Request(fromListening);
            await Task.WhenAll(toListening, toConnecting).WaitAsync(waitLimit);

            Assert.Equal(fromConnecting, await receivedByListening.Task);
            Assert.Equal(fromListening, await receivedByConnecting.Task);
            Assert.Single(listening.Connections);
            Assert.Single(connecting.Connections);
        }
    }

    /// <summary>A tagged send's package is found through the owning peer without knowing which connection carried it, and survives the connection that carried it disconnecting.</summary>
    [Fact]
    public async Task GetPackage_TaggedSendOnEitherSide_FoundOnOwnerAndSurvivesAfterDisconnect()
    {
        (IMsmtSessionPeer listening, IMsmtSessionPeer connecting, IMsmtConnection connectingSide, IMsmtConnection listeningSide) = await ConnectPair();
        await using (listening)
        await using (connecting)
        {
            listening.Receiver = (connection, payload, isResponseRequested) => new ValueTask<MsmtReceiveResult?>(MsmtReceiveResult.Accept());
            connecting.Receiver = (connection, payload, isResponseRequested) => new ValueTask<MsmtReceiveResult?>(MsmtReceiveResult.Accept());
            object connectingTag = new();
            object listeningTag = new();

            await connectingSide.Request("from connecting side"u8.ToArray(), new MsmtSendOptions { Tag = connectingTag }).WaitAsync(waitLimit);
            await listeningSide.Request("from listening side"u8.ToArray(), new MsmtSendOptions { Tag = listeningTag }).WaitAsync(waitLimit);

            Assert.Equal(MsmtSendStatus.Completed, connecting.GetPackage(connectingTag)!.Status);
            Assert.Equal(MsmtSendStatus.Completed, listening.GetPackage(listeningTag)!.Status);
            Assert.Null(listening.GetPackage(connectingTag));
            Assert.Null(connecting.GetPackage(listeningTag));

            // Disconnecting one side only guarantees that side has torn down; the other notices asynchronously
            // as its own read loop sees the socket close, so both must be awaited before checking either peer.
            Task<MsmtDisconnection> listeningSideDisconnected = WaitFor(listening.Disconnected, args => args.Connection == listeningSide);
            await connectingSide.DisposeAsync();
            await listeningSideDisconnected;

            Assert.Equal(MsmtSendStatus.Completed, connecting.GetPackage(connectingTag)!.Status);
            Assert.Equal(MsmtSendStatus.Completed, listening.GetPackage(listeningTag)!.Status);
        }
    }

    /// <summary>While a tagged send is still active, it appears in the owning peer's <c>Packages</c>, and can be cancelled from there.</summary>
    [Fact]
    public async Task Packages_ActiveTaggedSend_ListedOnOwnerAndCancellableFromThere()
    {
        (IMsmtSessionPeer listening, IMsmtSessionPeer connecting, IMsmtConnection connectingSide, IMsmtConnection listeningSide) = await ConnectPair();
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

            Assert.Contains(connecting.Packages, package => ReferenceEquals(package.Tag, tag));
            connecting.GetPackage(tag)!.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(waitLimit));
        }
    }

    private async Task<(IMsmtSessionPeer Listening, IMsmtSessionPeer Connecting, IMsmtConnection ConnectingSide, IMsmtConnection ListeningSide)> ConnectPair(Func<MsmtSessionPeerOptions, MsmtSessionPeerOptions>? listening = null, Func<MsmtSessionPeerOptions, MsmtSessionPeerOptions>? connecting = null)
    {
        (X509Certificate2 connectingCertificate, X509Certificate2 listeningCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        MsmtSessionPeer listeningPeer = new(listening is null ? Options(listeningCertificate, trustedAuthorities) : listening(Options(listeningCertificate, trustedAuthorities)));
        MsmtSessionPeer connectingPeer = new(connecting is null ? Options(connectingCertificate, trustedAuthorities) : connecting(Options(connectingCertificate, trustedAuthorities)));
        Task<IMsmtConnection> accepted = WaitFor(listeningPeer.Connected);
        listeningPeer.StartListener(0, "127.0.0.1");

        IMsmtConnection connectingSide = connectingPeer.Connect(Target(listeningPeer));
        Assert.True(await connectingSide.Wait().WaitAsync(waitLimit));
        return (listeningPeer, connectingPeer, connectingSide, await accepted);
    }

    private MsmtSessionPeerOptions Options(X509Certificate2 certificate, X509Certificate2Collection trustedAuthorities) => new()
    {
        Credentials = new MsmtCredentials { Identity = certificate, TrustedAuthorities = trustedAuthorities },
        RequireFullyQualifiedHostname = false,
    };

    private MsmtNameTarget Target(IMsmtSessionPeer peer) => new() { Host = peer.Listener!.Host, Port = peer.Listener!.Port, ServerName = peer.Listener!.Host };

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
}
