namespace BlueHeighliner.Msmt.Tests.Integration;

/// <summary>Integration tests for <see cref="MsmtClient"/> and <see cref="MsmtServer"/> exchanging real MSMT messages over TLS.</summary>
public sealed class MsmtClientServerTests
{
    /// <summary>A client sending a message in Message Mode receives the server's automatic acknowledgement.</summary>
    [Fact]
    public async Task Send_MessageMode_ReceivesAcknowledgement()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        MsmtResponse response = await SendAndWaitForResponse(client, "hello"u8.ToArray());

        Assert.True(response.Success);
    }

    /// <summary><see cref="MsmtClient.Dispose"/> immediately closes the connection without waiting for its background loops to finish.</summary>
    [Fact]
    public async Task Dispose_ImmediatelyClosesConnection()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        MsmtClient client = new();

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.Session,
            SessionLifetime = TimeSpan.FromSeconds(30),
        });

        await SendAndWaitForResponse(client, "hello"u8.ToArray());

        client.Dispose();
    }

    /// <summary>A server's <see cref="MsmtServer.Linked"/>, <see cref="MsmtServer.Received"/>, and <see cref="MsmtServer.Unlinked"/> events all carry the same <see cref="IMsmtLink"/> instance for a given client connection, and its <see cref="MsmtServerConnection.IsConnected"/> reflects the connection's actual lifecycle.</summary>
    [Fact]
    public async Task Send_MessageMode_ConnectedReceivedAndDisconnectedShareSameConnectionInstance()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();

        TaskCompletionSource<IMsmtLink> connected = new();
        TaskCompletionSource<MsmtReceivedEventArgs> receivedSource = new();
        TaskCompletionSource<MsmtServerConnection> disconnected = new();
        server.Linked += (_, args) => connected.TrySetResult(args.Link);
        server.Received += (_, message) => receivedSource.TrySetResult(message);
        server.Unlinked += (_, args) => disconnected.TrySetResult((MsmtServerConnection)args.Link);
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        Assert.False(client.IsConnected);

        client.Send("hello"u8.ToArray());

        IMsmtLink connectedLink = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        MsmtReceivedEventArgs received = await receivedSource.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(connectedLink, received.Link);

        MsmtServerConnection disconnectedConnection = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(connectedLink, disconnectedConnection);
        Assert.False(disconnectedConnection.IsConnected);

        // Message Mode tears the connection down immediately after each send.
        Assert.False(client.IsConnected);
    }

    /// <summary>A client's <see cref="IMsmtLink.Identity"/> is extracted from the server's certificate it verified, and the server's accepted connection's <see cref="IMsmtLink.Identity"/> is extracted from the client's certificate it verified.</summary>
    [Fact]
    public async Task Send_MessageMode_ConnectionIdentitiesMatchRemotePeerCertificate()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        TaskCompletionSource<IMsmtLink> serverAccepted = new();
        server.Linked += (_, args) => serverAccepted.TrySetResult(args.Link);
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await SendAndWaitForResponse(client, "hello"u8.ToArray());
        IMsmtLink acceptedLink = await serverAccepted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(serverCertificate.Thumbprint, client.Identity?.Thumbprint);
        Assert.Equal(clientCertificate.Thumbprint, acceptedLink.Identity?.Thumbprint);
    }

    /// <summary>A client raises <see cref="MsmtClient.Linked"/> with itself as the link and <see cref="IMsmtLink.Kind"/> set to <see cref="MsmtLinkType.Sender"/>, while the server it connects to raises <see cref="MsmtServer.Linked"/> with <see cref="IMsmtLink.Kind"/> set to <see cref="MsmtLinkType.Receiver"/>.</summary>
    [Fact]
    public async Task Send_MessageMode_ClientAndServerRaiseConnectedWithCorrectKind()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();

        TaskCompletionSource<MsmtLinkedEventArgs> serverConnected = new();
        server.Linked += (_, args) => serverConnected.TrySetResult(args);
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();

        TaskCompletionSource<MsmtLinkedEventArgs> clientConnected = new();
        client.Linked += (_, args) => clientConnected.TrySetResult(args);

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        client.Send("hello"u8.ToArray());

        MsmtLinkedEventArgs clientArgs = await clientConnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(client, clientArgs.Link);
        Assert.Equal(MsmtLinkType.Sender, clientArgs.Link.Kind);

        MsmtLinkedEventArgs serverArgs = await serverConnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MsmtLinkType.Receiver, serverArgs.Link.Kind);
    }

    /// <summary>A tagged <see cref="IMsmtPeer.Request"/> raises <see cref="IMsmtPeer.PackageChanged"/> for its tag through Queued, Transmitting, PendingAcknowledgement, and Completed in order, while an untagged request raises nothing.</summary>
    [Fact]
    public async Task Request_TaggedPayload_RaisesPackageChangedInOrder()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();

        List<(object Tag, MsmtSendStatus Status)> statusChanges = [];
        object tag = new();
        TaskCompletionSource taggedCompleted = new();
        client.PackageChanged += (_, args) =>
        {
            statusChanges.Add((args.Package.Tag, args.Status));
            if (ReferenceEquals(args.Package.Tag, tag) && args.Status == MsmtSendStatus.Completed)
            {
                taggedCompleted.TrySetResult();
            }
        };

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        _ = client.Request("hello"u8.ToArray(), new MsmtSendOptions { Tag = tag });
        await taggedCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
        [
            (tag, MsmtSendStatus.Queued),
            (tag, MsmtSendStatus.Transmitting),
            (tag, MsmtSendStatus.PendingAcknowledgement),
            (tag, MsmtSendStatus.Completed),
        ], statusChanges);

        statusChanges.Clear();
        await SendAndWaitForResponse(client, "world"u8.ToArray());

        Assert.Empty(statusChanges);
    }

    /// <summary>A tagged <see cref="MsmtClient.Send"/>, which never requests an acknowledgement, skips PendingAcknowledgement, moving straight from Transmitting to Completed.</summary>
    [Fact]
    public async Task Send_TaggedPayload_SkipsPendingAcknowledgementStatus()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();

        List<(object Tag, MsmtSendStatus Status)> statusChanges = [];
        TaskCompletionSource completed = new();
        client.PackageChanged += (_, args) =>
        {
            statusChanges.Add((args.Package.Tag, args.Status));
            if (args.Status == MsmtSendStatus.Completed)
            {
                completed.TrySetResult();
            }
        };

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        object tag = new();
        client.Send("hello"u8.ToArray(), new MsmtSendOptions { Tag = tag });
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
        [
            (tag, MsmtSendStatus.Queued),
            (tag, MsmtSendStatus.Transmitting),
            (tag, MsmtSendStatus.Completed),
        ], statusChanges);
    }

    /// <summary><see cref="MsmtClient.Cancel"/>ling a tagged send still sitting in the queue moves it straight from Queued to Cancelled, never transmitting it, and <see cref="MsmtClient.GetStatus"/> reflects the final state.</summary>
    [Fact]
    public async Task Cancel_QueuedPayloadBeforeConnect_RaisesCancelledStatusAndNeverTransmits()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        List<string> received = [];
        server.Received += (_, args) =>
        {
            lock (received)
            {
                received.Add(Encoding.ASCII.GetString(args.Payload.Memory.Span));
            }
        };
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();

        List<(object Tag, MsmtSendStatus Status)> statusChanges = [];
        client.PackageChanged += (_, args) => statusChanges.Add((args.Package.Tag, args.Status));

        object tag = new();

        // Queued and cancelled before Connect() starts the background processing loop, so the cancellation
        // is guaranteed to land while the item is still sitting in the queue rather than racing a
        // concurrently running loop.
        client.Send("cancelled"u8.ToArray(), new MsmtSendOptions { Tag = tag });
        client.Cancel(tag);

        Assert.Equal(MsmtSendStatus.Queued, client.GetStatus(tag));

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await SendAndWaitForResponse(client, "not-cancelled"u8.ToArray());

        Assert.Equal([(tag, MsmtSendStatus.Queued), (tag, MsmtSendStatus.Cancelled)], statusChanges);
        Assert.Equal(MsmtSendStatus.Cancelled, client.GetStatus(tag));
        Assert.DoesNotContain("cancelled", received);
    }

    /// <summary>
    /// Cancelling a tagged <see cref="MsmtClient.Request"/> while it is actively waiting on the remote
    /// peer's acknowledgement (not merely still queued) unblocks that wait, closes the connection, faults
    /// the returned <see cref="Task{TResult}"/> with cancellation, and reports a final <see
    /// cref="MsmtSendStatus.Cancelled"/> status.
    /// </summary>
    [Fact]
    public async Task Cancel_TaggedRequestAwaitingAcknowledgement_UnblocksReadAndClosesConnection()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        TaskCompletionSource<IMsmtResponder> deferredResponder = new();
        server.Received += (_, args) =>
        {
            args.Responder.Defer();
            deferredResponder.TrySetResult(args.Responder);
        };
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();
        TaskCompletionSource<MsmtUnlinkedEventArgs> unlinked = new();
        client.Unlinked += (_, args) => unlinked.TrySetResult(args);

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        object tag = new();
        Task<MsmtResponse> responseTask = client.Request("hello"u8.ToArray(), new MsmtSendOptions { Tag = tag });

        // The server has deferred but never decided, so the client is blocked reading the acknowledgement
        // that will never come - the only way out is cancellation.
        await deferredResponder.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.False(responseTask.IsCompleted);

        client.Cancel(tag);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => responseTask).WaitAsync(TimeSpan.FromSeconds(5));
        await unlinked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MsmtSendStatus.Cancelled, client.GetStatus(tag));
    }

    /// <summary>
    /// Cancelling <see cref="MsmtClient.Request"/>'s own <see cref="CancellationToken"/> parameter - not
    /// <see cref="MsmtClient.Cancel"/> - while actively waiting on the remote peer's acknowledgement also
    /// unblocks that wait and faults the returned <see cref="Task{TResult}"/> with cancellation, exactly
    /// like <see cref="Cancel_TaggedRequestAwaitingAcknowledgement_UnblocksReadAndClosesConnection"/>.
    /// </summary>
    [Fact]
    public async Task Request_CancellationTokenCancelledWhileAwaitingAcknowledgement_UnblocksReadAndFaultsTask()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        TaskCompletionSource<IMsmtResponder> deferredResponder = new();
        server.Received += (_, args) =>
        {
            args.Responder.Defer();
            deferredResponder.TrySetResult(args.Responder);
        };
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();
        TaskCompletionSource<MsmtUnlinkedEventArgs> unlinked = new();
        client.Unlinked += (_, args) => unlinked.TrySetResult(args);

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        using CancellationTokenSource cancellation = new();
        Task<MsmtResponse> responseTask = client.Request("hello"u8.ToArray(), cancellation: cancellation.Token);

        await deferredResponder.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.False(responseTask.IsCompleted);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => responseTask).WaitAsync(TimeSpan.FromSeconds(5));
        await unlinked.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Disposing a client that still has a payload sitting in its queue (e.g. because <see cref="MsmtClient.Connect"/> was never called) cancels it rather than abandoning it, disposing its payload and reporting a final <see cref="MsmtSendStatus.Cancelled"/> status.</summary>
    [Fact]
    public void Dispose_PayloadStillQueued_CancelsAndDisposesIt()
    {
        MsmtClient client = new();

        List<(object Tag, MsmtSendStatus Status)> statusChanges = [];
        client.PackageChanged += (_, args) => statusChanges.Add((args.Package.Tag, args.Status));

        object tag = new();
        client.Send("never-sent"u8.ToArray(), new MsmtSendOptions { Tag = tag });

        client.Dispose();

        Assert.Equal([(tag, MsmtSendStatus.Queued), (tag, MsmtSendStatus.Cancelled)], statusChanges);
        Assert.Equal(MsmtSendStatus.Cancelled, client.GetStatus(tag));
    }

    /// <summary><see cref="MsmtClient.GetStatus"/> returns <see langword="null"/> for a tag no send was ever queued with, and <see cref="MsmtClient.Cancel"/> does nothing for it.</summary>
    [Fact]
    public async Task GetStatus_UnknownTag_ReturnsNull()
    {
        await using MsmtClient client = new();
        object tag = new();

        Assert.Null(client.GetStatus(tag));
        client.Cancel(tag);
        Assert.Null(client.GetStatus(tag));
    }

    /// <summary>Payloads queued via <see cref="IMsmtPeer.Send"/> are transmitted in descending priority order and, within a priority, first-in-first-out.</summary>
    [Fact]
    public async Task Send_MultiplePrioritiesQueuedBeforeConnect_TransmitsInPriorityThenFifoOrder()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();

        List<string> receivedOrder = [];
        TaskCompletionSource allReceived = new();
        server.Received += (_, args) =>
        {
            lock (receivedOrder)
            {
                receivedOrder.Add(Encoding.ASCII.GetString(args.Payload.Memory.Span));
                if (receivedOrder.Count >= 5)
                {
                    allReceived.TrySetResult();
                }
            }
        };
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();

        // Queued before Connect() starts the background processing loop, so every send lands in the
        // priority queue before any of them can be transmitted, making the resulting order deterministic.
        client.Send(Encoding.ASCII.GetBytes("low-1"), new MsmtSendOptions { Priority = -1 });
        client.Send(Encoding.ASCII.GetBytes("high-1"), new MsmtSendOptions { Priority = 5 });
        client.Send(Encoding.ASCII.GetBytes("normal-1"));
        client.Send(Encoding.ASCII.GetBytes("high-2"), new MsmtSendOptions { Priority = 5 });
        client.Send(Encoding.ASCII.GetBytes("normal-2"));

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.Session,
            SessionLifetime = TimeSpan.FromSeconds(30),
        });

        await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["high-1", "high-2", "normal-1", "normal-2", "low-1"], receivedOrder);
    }

    /// <summary>A tagged send with <see cref="MsmtSendOptions.Dscp"/> set marks the underlying socket with the requested DSCP value before transmitting.</summary>
    [Fact]
    public async Task Send_WithDscp_MarksSocketBeforeTransmitting()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();

        object tag = new();
        TaskCompletionSource<int> observedTos = new();
        client.PackageChanged += (_, args) =>
        {
            if (ReferenceEquals(args.Package.Tag, tag) && args.Status == MsmtSendStatus.Transmitting)
            {
                observedTos.TrySetResult((int)client.Socket!.GetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService)!);
            }
        };

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        const int expeditedForwarding = 46;
        await SendAndWaitForResponse(client, "hello"u8.ToArray(), new MsmtSendOptions { Tag = tag, Dscp = expeditedForwarding });

        int tos = await observedTos.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expeditedForwarding << 2, tos);
    }

    /// <summary>Session Mode reuses one connection across sends requesting different <see cref="MsmtSendOptions.Dscp"/> values, re-marking the socket for each.</summary>
    [Fact]
    public async Task Send_SessionModeWithDifferentDscpPerSend_ReMarksSocketEachTime()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        int messagesReceived = 0;
        TaskCompletionSource allReceived = new();
        server.Received += (_, _) =>
        {
            if (Interlocked.Increment(ref messagesReceived) >= 2)
            {
                allReceived.TrySetResult();
            }
        };
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();

        List<int> observedTos = [];
        client.PackageChanged += (_, args) =>
        {
            if (args.Status == MsmtSendStatus.Transmitting)
            {
                lock (observedTos)
                {
                    observedTos.Add((int)client.Socket!.GetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService)!);
                }
            }
        };

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.Session,
            SessionLifetime = TimeSpan.FromSeconds(30),
        });

        const int expeditedForwarding = 46;
        const int assuredForwarding11 = 10;
        client.Send("one"u8.ToArray(), new MsmtSendOptions { Tag = new object(), Dscp = expeditedForwarding });
        client.Send("two"u8.ToArray(), new MsmtSendOptions { Tag = new object(), Dscp = assuredForwarding11 });

        await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([expeditedForwarding << 2, assuredForwarding11 << 2], observedTos);
    }

    /// <summary>A send with no <see cref="MsmtSendOptions.Dscp"/> leaves the socket's DSCP marking untouched (default).</summary>
    [Fact]
    public async Task Send_WithoutDscp_LeavesSocketUnmarked()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();

        object tag = new();
        TaskCompletionSource<int> observedTos = new();
        client.PackageChanged += (_, args) =>
        {
            if (ReferenceEquals(args.Package.Tag, tag) && args.Status == MsmtSendStatus.Transmitting)
            {
                observedTos.TrySetResult((int)client.Socket!.GetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService)!);
            }
        };

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await SendAndWaitForResponse(client, "hello"u8.ToArray(), new MsmtSendOptions { Tag = tag });

        int tos = await observedTos.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, tos);
    }

    /// <summary>A <see cref="MsmtClient.Request"/> resolves with the server's success and response payload.</summary>
    [Fact]
    public async Task Request_Sent_ReturnsSuccessAndPayload()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        MsmtResponse response = await SendAndWaitForResponse(client, "hello"u8.ToArray());

        Assert.True(response.Success);
        Assert.Empty(response.Payload.Memory.ToArray());
    }

    /// <summary>A <see cref="MsmtServer.Received"/> subscriber that calls <see cref="IMsmtResponder.Accept(IMemoryOwner{byte})"/> with a payload carries it through to the client's <see cref="MsmtResponse.Payload"/>.</summary>
    [Fact]
    public async Task Request_ReceivedHandlerAcceptsWithPayload_PayloadPropagatesToResponse()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Received += (_, args) => args.Responder.Accept("ack-payload"u8.ToArray());
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        MsmtResponse response = await SendAndWaitForResponse(client, "hello"u8.ToArray());

        Assert.True(response.Success);
        Assert.Equal("ack-payload"u8.ToArray(), response.Payload.Memory.ToArray());
    }

    /// <summary>A <see cref="MsmtServer.Received"/> subscriber that uses <see cref="MsmtReceivedEventArgs.Responder"/> for a message sent via <see cref="MsmtClient.Send"/>, which never requests a response, throws and closes the connection.</summary>
    [Fact]
    public async Task Send_ReceivedHandlerUsesResponder_ServerClosesConnectionWithException()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Received += (_, args) =>
        {
            Assert.False(args.IsResponseRequested);
            args.Responder.Accept();
        };
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        MsmtUnlinkedEventArgs disconnection = await SendAndWaitForDisconnection(client, "hello"u8.ToArray());

        Assert.NotNull(disconnection.Exception);
    }

    /// <summary>
    /// A <see cref="MsmtServer.Received"/> subscriber that calls <see cref="IMsmtResponder.Defer"/> leaves
    /// the message unacknowledged - and the client's <see cref="MsmtClient.Request"/> unresolved - until
    /// something else later calls <see cref="IMsmtResponder.Accept(IMemoryOwner{byte})"/>, even after the
    /// subscriber that deferred it has already returned.
    /// </summary>
    [Fact]
    public async Task Request_ReceivedHandlerDefers_ResponseWaitsUntilLaterAccept()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        TaskCompletionSource<IMsmtResponder> deferredResponder = new();
        server.Received += (_, args) =>
        {
            args.Responder.Defer();
            deferredResponder.TrySetResult(args.Responder);
        };
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        Task<MsmtResponse> responseTask = client.Request("hello"u8.ToArray());

        IMsmtResponder responder = await deferredResponder.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The subscriber that called Defer has already returned by this point; nothing has decided the
        // response yet, so the request stays pending.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.False(responseTask.IsCompleted);

        responder.Accept("deferred-payload"u8.ToArray());

        MsmtResponse response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(response.Success);
        Assert.Equal("deferred-payload"u8.ToArray(), response.Payload.Memory.ToArray());
    }

    /// <summary>A <see cref="MsmtServer.Received"/> subscriber that calls <see cref="IMsmtResponder.Reject()"/> causes the server to negatively acknowledge the message.</summary>
    [Fact]
    public async Task Request_ReceivedHandlerRejects_ServerNegativelyAcknowledges()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Received += (_, args) => args.Responder.Reject();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        MsmtResponse response = await SendAndWaitForResponse(client, "hello"u8.ToArray());

        Assert.False(response.Success);
    }

    /// <summary>When multiple <see cref="MsmtServer.Received"/> subscribers are registered, any one of them rejecting the message negatively acknowledges it.</summary>
    [Fact]
    public async Task Request_OneOfMultipleReceivedHandlersRejects_ServerNegativelyAcknowledges()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        bool secondHandlerRan = false;
        server.Received += (_, _) => { };
        server.Received += (_, args) =>
        {
            secondHandlerRan = true;
            args.Responder.Reject();
        };
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        MsmtResponse response = await SendAndWaitForResponse(client, "hello"u8.ToArray());

        Assert.True(secondHandlerRan);
        Assert.False(response.Success);
    }

    /// <summary>A message received on a connection with no <see cref="MsmtServer.Received"/> subscriber is acknowledged.</summary>
    [Fact]
    public async Task Send_NoReceivedSubscriber_ServerAcknowledges()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        MsmtResponse response = await SendAndWaitForResponse(client, "hello"u8.ToArray());

        Assert.True(response.Success);
    }

    /// <summary>A Message Mode send raises <see cref="MsmtServer.Received"/> on the server for the message, resolves the <see cref="MsmtClient.Request(IMemoryOwner{byte}, MsmtSendOptions?, CancellationToken)"/> task on the client for its acknowledgement, and raises <see cref="MsmtServer.Unlinked"/>/<see cref="MsmtClient.Unlinked"/> on both sides once the connection tears down afterward.</summary>
    [Fact]
    public async Task Request_MessageMode_RaisesReceivedAndResolvesResponseAndDisconnectedOnBothSides()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();

        TaskCompletionSource<MsmtReceivedEventArgs> serverReceived = new();
        TaskCompletionSource serverDisconnected = new();
        server.Received += (_, message) => serverReceived.TrySetResult(message);
        server.Unlinked += (_, _) => serverDisconnected.TrySetResult();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();

        TaskCompletionSource clientDisconnected = new();
        client.Unlinked += (_, _) => clientDisconnected.TrySetResult();

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        Task<MsmtResponse> responseTask = client.Request("hello"u8.ToArray());

        MsmtReceivedEventArgs receivedByServer = await serverReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hello"u8.ToArray(), receivedByServer.Payload.Memory.ToArray());

        MsmtResponse respondedToClient = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(respondedToClient.Payload.Memory.ToArray());

        await serverDisconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await clientDisconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>A reachability check against a running server succeeds without raising <see cref="IMsmtPeer.Received"/>.</summary>
    [Fact]
    public async Task Reach_ServerRunning_ReturnsTrueWithoutRaisingReceived()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();

        bool receivedRaised = false;
        server.Received += (_, _) => receivedRaised = true;
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtPeer peer = new(new MsmtOptions
        {
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        bool reachable = await peer.Test(new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port });

        Assert.True(reachable);
        Assert.False(receivedRaised);
    }

    /// <summary>A Session Mode client reuses a single negotiated connection across multiple sends.</summary>
    [Fact]
    public async Task Send_SessionMode_ReusesConnectionAcrossMultipleSends()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();

        int messagesReceived = 0;
        TaskCompletionSource allReceived = new();
        server.Received += (_, _) =>
        {
            if (Interlocked.Increment(ref messagesReceived) >= 2)
            {
                allReceived.TrySetResult();
            }
        };
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.Session,
            SessionLifetime = TimeSpan.FromSeconds(30),
        });

        client.Send("one"u8.ToArray());
        client.Send("two"u8.ToArray());

        await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, messagesReceived);
    }

    /// <summary>A Message Mode with Rekeying client keeps working across TLS 1.3 key updates and a forced reconnect once its rekey limit is exceeded.</summary>
    [Fact]
    public async Task Send_MessageWithRekeying_SucceedsAcrossRekeysAndForcedReconnect()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();

        int messagesReceived = 0;
        server.Received += (_, _) => Interlocked.Increment(ref messagesReceived);
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.MessageWithRekeying,
            RekeyLimit = 2,
        });

        List<Task<MsmtResponse>> requests = [];
        for (int i = 0; i < 5; i++)
        {
            requests.Add(client.Request(Encoding.ASCII.GetBytes($"message-{i}")));
        }

        MsmtResponse[] responses = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(5, messagesReceived);
        Assert.All(responses, response => Assert.True(response.Success));
    }

    /// <summary>A client rejects a server certificate signed by an authority it does not trust, raising <see cref="MsmtClient.LinkFailed"/> rather than <see cref="MsmtClient.Linked"/> or <see cref="MsmtClient.Unlinked"/>, since the connection never completed its handshake.</summary>
    [Fact]
    public async Task Send_ServerCertificateUntrusted_RaisesConnectionFailed()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection serverTrustedAuthorities) = TestMsmtCertificates.Create();
        (_, _, X509Certificate2Collection unrelatedTrustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = serverTrustedAuthorities },
        });

        await using MsmtClient client = new();
        bool connectedRaised = false;
        bool disconnectedRaised = false;
        client.Linked += (_, _) => connectedRaised = true;
        client.Unlinked += (_, _) => disconnectedRaised = true;

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = unrelatedTrustedAuthorities },
        });

        MsmtLinkFailedEventArgs failure = await SendAndWaitForConnectionFailure(client, "hello"u8.ToArray());

        Assert.Same(client, failure.Link);
        Assert.Equal(MsmtLinkType.Sender, failure.Link.Kind);
        Assert.NotNull(failure.Exception);
        Assert.False(connectedRaised);
        Assert.False(disconnectedRaised);
    }

    /// <summary>A server rejects a client certificate signed by an authority it does not trust, and the client observes this as <see cref="MsmtClient.LinkFailed"/> rather than <see cref="MsmtClient.Linked"/> or <see cref="MsmtClient.Unlinked"/>, since the connection never completed its handshake.</summary>
    [Fact]
    public async Task Send_ClientCertificateUntrusted_ServerRejectsHandshake()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        (_, _, X509Certificate2Collection unrelatedTrustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = unrelatedTrustedAuthorities },
        });

        await using MsmtClient client = new();
        bool connectedRaised = false;
        bool disconnectedRaised = false;
        client.Linked += (_, _) => connectedRaised = true;
        client.Unlinked += (_, _) => disconnectedRaised = true;

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        MsmtLinkFailedEventArgs failure = await SendAndWaitForConnectionFailure(client, "hello"u8.ToArray());

        Assert.Same(client, failure.Link);
        Assert.Equal(MsmtLinkType.Sender, failure.Link.Kind);
        Assert.NotNull(failure.Exception);
        Assert.False(connectedRaised);
        Assert.False(disconnectedRaised);
    }

    /// <summary>Negotiating Session Mode against a server that does not support it fails.</summary>
    [Fact]
    public async Task Send_SessionModeUnsupportedByServer_Throws()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
            SupportsSessionMode = false,
        });

        await using MsmtClient client = new();

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.Session,
        });

        MsmtUnlinkedEventArgs disconnection = await SendAndWaitForDisconnection(client, "hello"u8.ToArray());

        Assert.IsType<InvalidOperationException>(disconnection.Exception);
    }

    /// <summary>A Session Mode client transparently renegotiates a new connection once its previous session has expired.</summary>
    [Fact]
    public async Task Send_SessionMode_ExpiredLifetimeTriggersRenegotiation()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
            MaximumSessionLifetime = TimeSpan.FromSeconds(1),
        });

        await using MsmtClient client = new();

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.Session,
            SessionLifetime = TimeSpan.FromSeconds(1),
        });

        MsmtResponse first = await SendAndWaitForResponse(client, "one"u8.ToArray());
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        MsmtResponse second = await SendAndWaitForResponse(client, "two"u8.ToArray());

        Assert.True(first.Success);
        Assert.True(second.Success);
    }

    /// <summary>A <see cref="MsmtClient.Send"/>, which never requests an acknowledgement, still completes successfully, without the connection ever reporting an error.</summary>
    [Fact]
    public async Task Send_StillSucceeds()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await using MsmtClient client = new();

        bool disconnectedWithException = false;
        client.Unlinked += (_, args) => disconnectedWithException |= args.Exception is not null;

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        await SendAndWaitForCompletion(client, "hello"u8.ToArray());

        Assert.False(disconnectedWithException);
    }

    /// <summary>A second Session Mode negotiation on an already-negotiated connection is rejected without closing the connection.</summary>
    [Fact]
    public async Task RawConnection_DuplicateSessionNegotiation_ServerRejectsSecondRequestButKeepsConnectionOpen()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        MsmtConnectOptions clientOptions = new()
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        };

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(clientOptions.Target.Host, clientOptions.Target.Port);

        RekeyableTlsClientProtocol protocol = new(tcpClient.GetStream());
        protocol.Connect(new MsmtTlsClient(clientOptions));
        Stream stream = protocol.Stream;

        byte[] negotiationPayload = Encoding.ASCII.GetBytes("10");
        MsmtHeader negotiationHeader = new()
        {
            Version = MsmtHeader.SupportedVersion,
            Flags = MsmtMessageFlags.SessionModeNegotiation,
            MessageId = MsmtProtocol.GenerateMessageId(),
            Length = (uint)negotiationPayload.Length,
        };

        (MsmtHeader firstResponse, _) = await ExchangeRawAsync(stream, negotiationHeader, negotiationPayload);
        (MsmtHeader secondResponse, _) = await ExchangeRawAsync(stream, negotiationHeader with { MessageId = MsmtProtocol.GenerateMessageId() }, negotiationPayload);

        protocol.Close();

        Assert.Equal(MsmtMessageFlags.SessionModeAccepted, firstResponse.Flags);
        Assert.Equal(MsmtMessageFlags.SessionModeRejected, secondResponse.Flags);
    }

    /// <summary>Per the ICD, Session Mode Negotiation is only honored as the very first message on a connection; a negotiation attempt sent after a regular message is rejected without closing the connection.</summary>
    [Fact]
    public async Task RawConnection_SessionModeNegotiationNotFirstMessage_ServerRejectsWithoutClosing()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        MsmtConnectOptions clientOptions = new()
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        };

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(clientOptions.Target.Host, clientOptions.Target.Port);

        RekeyableTlsClientProtocol protocol = new(tcpClient.GetStream());
        protocol.Connect(new MsmtTlsClient(clientOptions));
        Stream stream = protocol.Stream;

        MsmtHeader plainHeader = new()
        {
            Version = MsmtHeader.SupportedVersion,
            Flags = MsmtMessageFlags.AcknowledgementRequestedOrGiven,
            MessageId = MsmtProtocol.GenerateMessageId(),
            Length = 0,
        };

        byte[] negotiationPayload = Encoding.ASCII.GetBytes("10");
        MsmtHeader negotiationHeader = new()
        {
            Version = MsmtHeader.SupportedVersion,
            Flags = MsmtMessageFlags.SessionModeNegotiation | MsmtMessageFlags.MessageSuccess,
            MessageId = MsmtProtocol.GenerateMessageId(),
            Length = (uint)negotiationPayload.Length,
        };

        (MsmtHeader firstResponse, _) = await ExchangeRawAsync(stream, plainHeader, []);
        (MsmtHeader secondResponse, _) = await ExchangeRawAsync(stream, negotiationHeader, negotiationPayload);
        (MsmtHeader thirdResponse, _) = await ExchangeRawAsync(stream, plainHeader with { MessageId = MsmtProtocol.GenerateMessageId() }, []);

        protocol.Close();

        Assert.Equal(MsmtMessageFlags.MessageSuccess | MsmtMessageFlags.AcknowledgementRequestedOrGiven, firstResponse.Flags);
        Assert.Equal(MsmtMessageFlags.SessionModeRejected, secondResponse.Flags);
        Assert.Equal(MsmtMessageFlags.MessageSuccess | MsmtMessageFlags.AcknowledgementRequestedOrGiven, thirdResponse.Flags);
    }

    /// <summary>
    /// A message sent without requesting an acknowledgement still gets <see
    /// cref="MsmtMessageFlags.MessageSuccess"/> on its wire response - there is no ack-based path through
    /// which the application could report otherwise - but never <see
    /// cref="MsmtMessageFlags.AcknowledgementRequestedOrGiven"/>, since none was requested.
    /// </summary>
    [Fact]
    public async Task RawConnection_MessageWithoutAcknowledgementRequested_ServerRespondsWithMessageSuccessOnly()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        MsmtConnectOptions clientOptions = new()
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        };

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(clientOptions.Target.Host, clientOptions.Target.Port);

        RekeyableTlsClientProtocol protocol = new(tcpClient.GetStream());
        protocol.Connect(new MsmtTlsClient(clientOptions));

        MsmtHeader unacknowledgedHeader = new()
        {
            Version = MsmtHeader.SupportedVersion,
            Flags = MsmtMessageFlags.None,
            MessageId = MsmtProtocol.GenerateMessageId(),
            Length = 0,
        };

        (MsmtHeader response, _) = await ExchangeRawAsync(protocol.Stream, unacknowledgedHeader, []);

        protocol.Close();

        Assert.Equal(MsmtMessageFlags.MessageSuccess, response.Flags);
    }

    /// <summary>A Session Mode negotiation payload that isn't a positive integer - non-numeric, or a negative/zero lifetime - is rejected as unsupported rather than accepted with an already- (or immediately-) expired session.</summary>
    [Fact]
    public async Task RawConnection_SessionNegotiationNonPositiveOrMalformedLifetime_ServerRejects()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        MsmtConnectOptions clientOptions = new()
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        };

        using TcpClient malformedTcpClient = new();
        await malformedTcpClient.ConnectAsync(clientOptions.Target.Host, clientOptions.Target.Port);
        RekeyableTlsClientProtocol malformedProtocol = new(malformedTcpClient.GetStream());
        malformedProtocol.Connect(new MsmtTlsClient(clientOptions));

        byte[] malformedPayload = Encoding.ASCII.GetBytes("not-a-number");
        MsmtHeader malformedHeader = new()
        {
            Version = MsmtHeader.SupportedVersion,
            Flags = MsmtMessageFlags.SessionModeNegotiation,
            MessageId = MsmtProtocol.GenerateMessageId(),
            Length = (uint)malformedPayload.Length,
        };

        (MsmtHeader malformedResponse, _) = await ExchangeRawAsync(malformedProtocol.Stream, malformedHeader, malformedPayload);
        malformedProtocol.Close();

        using TcpClient negativeTcpClient = new();
        await negativeTcpClient.ConnectAsync(clientOptions.Target.Host, clientOptions.Target.Port);
        RekeyableTlsClientProtocol negativeProtocol = new(negativeTcpClient.GetStream());
        negativeProtocol.Connect(new MsmtTlsClient(clientOptions));

        byte[] negativePayload = Encoding.ASCII.GetBytes("-5");
        MsmtHeader negativeHeader = new()
        {
            Version = MsmtHeader.SupportedVersion,
            Flags = MsmtMessageFlags.SessionModeNegotiation,
            MessageId = MsmtProtocol.GenerateMessageId(),
            Length = (uint)negativePayload.Length,
        };

        (MsmtHeader negativeResponse, _) = await ExchangeRawAsync(negativeProtocol.Stream, negativeHeader, negativePayload);
        negativeProtocol.Close();

        Assert.Equal(MsmtMessageFlags.SessionModeUnsupported, malformedResponse.Flags);
        Assert.Equal(MsmtMessageFlags.SessionModeUnsupported, negativeResponse.Flags);
    }

    /// <summary>The server rejects a message using an unsupported API version, without processing it, and closes the connection.</summary>
    [Fact]
    public async Task RawConnection_UnsupportedVersion_ServerRejectsAndCloses()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        bool receivedRaised = false;
        server.Received += (_, _) => receivedRaised = true;
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        MsmtConnectOptions clientOptions = new()
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        };

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(clientOptions.Target.Host, clientOptions.Target.Port);

        RekeyableTlsClientProtocol protocol = new(tcpClient.GetStream());
        protocol.Connect(new MsmtTlsClient(clientOptions));
        Stream stream = protocol.Stream;

        MsmtHeader requestHeader = new()
        {
            Version = (byte)(MsmtHeader.SupportedVersion + 1),
            Flags = MsmtMessageFlags.AcknowledgementRequestedOrGiven,
            MessageId = MsmtProtocol.GenerateMessageId(),
            Length = 0,
        };

        (MsmtHeader response, byte[] payload) = await ExchangeRawAsync(stream, requestHeader, []);
        protocol.Close();

        Assert.Equal(MsmtMessageFlags.InvalidPreambleOrModeUnsupported, response.Flags);
        Assert.Empty(payload);
        Assert.False(receivedRaised);
    }

    /// <summary>The server rejects a message that sets a reserved flag bit, without processing it, and closes the connection.</summary>
    [Fact]
    public async Task RawConnection_ReservedFlagBitSet_ServerRejectsAndCloses()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        bool receivedRaised = false;
        server.Received += (_, _) => receivedRaised = true;
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        MsmtConnectOptions clientOptions = new()
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        };

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(clientOptions.Target.Host, clientOptions.Target.Port);

        RekeyableTlsClientProtocol protocol = new(tcpClient.GetStream());
        protocol.Connect(new MsmtTlsClient(clientOptions));
        Stream stream = protocol.Stream;

        MsmtHeader requestHeader = new()
        {
            Version = MsmtHeader.SupportedVersion,
            Flags = MsmtMessageFlags.AcknowledgementRequestedOrGiven | (MsmtMessageFlags)0x8000,
            MessageId = MsmtProtocol.GenerateMessageId(),
            Length = 0,
        };

        (MsmtHeader response, byte[] payload) = await ExchangeRawAsync(stream, requestHeader, []);
        protocol.Close();

        Assert.Equal(MsmtMessageFlags.InvalidPreambleOrModeUnsupported, response.Flags);
        Assert.Empty(payload);
        Assert.False(receivedRaised);
    }

    /// <summary>The server rejects a message that declares a payload length beyond the accepted maximum, without attempting to read it.</summary>
    [Fact]
    public async Task RawConnection_LengthExceedsMaximum_ServerRejectsWithoutReadingPayload()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        MsmtConnectOptions clientOptions = new()
        {
            Target = new MsmtTarget { Host = server.LocalEndPoint!.Address.ToString(), Port = server.LocalEndPoint!.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        };

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(clientOptions.Target.Host, clientOptions.Target.Port);

        RekeyableTlsClientProtocol protocol = new(tcpClient.GetStream());
        protocol.Connect(new MsmtTlsClient(clientOptions));
        Stream stream = protocol.Stream;

        MsmtHeader requestHeader = new()
        {
            Version = MsmtHeader.SupportedVersion,
            Flags = MsmtMessageFlags.AcknowledgementRequestedOrGiven,
            MessageId = MsmtProtocol.GenerateMessageId(),
            Length = MsmtHeader.MaxLength + 1,
        };

        byte[] headerBuffer = new byte[MsmtHeader.Size];
        requestHeader.Write(headerBuffer);
        await stream.WriteAsync(headerBuffer);

        await MsmtProtocol.ReadExact(stream, headerBuffer, CancellationToken.None);
        MsmtHeader response = MsmtHeader.Read(headerBuffer);
        protocol.Close();

        Assert.Equal(MsmtMessageFlags.InvalidPreambleOrModeUnsupported, response.Flags);
        Assert.Equal(0u, response.Length);
    }

    /// <summary>A client rejects an acknowledgement whose message ID does not correlate to the message it sent.</summary>
    [Fact]
    public async Task Send_AcknowledgementMessageIdMismatch_Throws()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        MsmtHostOptions serverOptions = new()
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        };

        TcpListener listener = new(MsmtProtocol.ResolveAddress(serverOptions.Host), serverOptions.Port);
        listener.Start();
        IPEndPoint boundEndPoint = (IPEndPoint)listener.LocalEndpoint;

        Task rogueServerTask = Task.Run(async () =>
        {
            using TcpClient acceptedClient = await listener.AcceptTcpClientAsync();
            Org.BouncyCastle.Tls.TlsServerProtocol protocol = new(acceptedClient.GetStream());
            protocol.Accept(new MsmtTlsServer(serverOptions));
            Stream stream = protocol.Stream;

            byte[] headerBuffer = new byte[MsmtHeader.Size];
            await MsmtProtocol.ReadExact(stream, headerBuffer, CancellationToken.None);
            MsmtHeader requestHeader = MsmtHeader.Read(headerBuffer);
            if (requestHeader.Length > 0)
            {
                await MsmtProtocol.ReadExact(stream, new byte[requestHeader.Length], CancellationToken.None);
            }

            MsmtHeader wrongResponse = new()
            {
                Version = MsmtHeader.SupportedVersion,
                Flags = MsmtMessageFlags.MessageSuccess,
                MessageId = unchecked((ushort)(requestHeader.MessageId + 1)),
                Length = 0,
            };
            wrongResponse.Write(headerBuffer);
            await stream.WriteAsync(headerBuffer);
            protocol.Close();
        });

        await using MsmtClient client = new();

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = boundEndPoint.Address.ToString(), Port = boundEndPoint.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        });

        MsmtUnlinkedEventArgs disconnection = await SendAndWaitForDisconnection(client, "hello"u8.ToArray());
        Assert.IsType<InvalidOperationException>(disconnection.Exception);

        await rogueServerTask;
        listener.Stop();
    }

    /// <summary>Per the ICD, a Session Mode client's initial negotiation message sets both the Session Mode Negotiation and Message Success flags.</summary>
    [Fact]
    public async Task Send_SessionMode_NegotiationRequestSetsMessageSuccessFlag()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        MsmtHostOptions serverOptions = new()
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        };

        TcpListener listener = new(MsmtProtocol.ResolveAddress(serverOptions.Host), serverOptions.Port);
        listener.Start();
        IPEndPoint boundEndPoint = (IPEndPoint)listener.LocalEndpoint;

        MsmtMessageFlags observedFlags = MsmtMessageFlags.None;

        Task rogueServerTask = Task.Run(async () =>
        {
            using TcpClient acceptedClient = await listener.AcceptTcpClientAsync();
            Org.BouncyCastle.Tls.TlsServerProtocol protocol = new(acceptedClient.GetStream());
            protocol.Accept(new MsmtTlsServer(serverOptions));
            Stream stream = protocol.Stream;

            byte[] headerBuffer = new byte[MsmtHeader.Size];
            await MsmtProtocol.ReadExact(stream, headerBuffer, CancellationToken.None);
            MsmtHeader requestHeader = MsmtHeader.Read(headerBuffer);
            observedFlags = requestHeader.Flags;
            if (requestHeader.Length > 0)
            {
                await MsmtProtocol.ReadExact(stream, new byte[requestHeader.Length], CancellationToken.None);
            }

            byte[] responsePayload = Encoding.ASCII.GetBytes("30");
            MsmtHeader acceptResponse = new()
            {
                Version = MsmtHeader.SupportedVersion,
                Flags = MsmtMessageFlags.SessionModeAccepted,
                MessageId = requestHeader.MessageId,
                Length = (uint)responsePayload.Length,
            };
            headerBuffer = new byte[MsmtHeader.Size];
            acceptResponse.Write(headerBuffer);
            await stream.WriteAsync(headerBuffer);
            await stream.WriteAsync(responsePayload);
            protocol.Close();
        });

        await using MsmtClient client = new();

        await client.Connect(new MsmtConnectOptions
        {
            Target = new MsmtTarget { Host = boundEndPoint.Address.ToString(), Port = boundEndPoint.Port },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.Session,
            SessionLifetime = TimeSpan.FromSeconds(30),
        });

        MsmtUnlinkedEventArgs disconnection = await SendAndWaitForDisconnection(client, "hello"u8.ToArray());
        Assert.NotNull(disconnection.Exception);

        await rogueServerTask;
        listener.Stop();

        Assert.Equal(MsmtMessageFlags.SessionModeNegotiation | MsmtMessageFlags.MessageSuccess, observedFlags);
    }

    /// <summary>The server rejects a TLS handshake from a client that does not present a "server_name" (SNI) extension.</summary>
    [Fact]
    public async Task RawConnection_NoServerNameExtension_ServerRejectsHandshake()
    {
        (X509Certificate2 serverCertificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(server.LocalEndPoint!.Address, server.LocalEndPoint!.Port);

        Org.BouncyCastle.Tls.TlsClientProtocol protocol = new(tcpClient.GetStream());

        Assert.Throws<Org.BouncyCastle.Tls.TlsFatalAlertReceived>(() => protocol.Connect(new NoSniTlsClient()));
    }

    /// <summary>The server rejects a TLS handshake from a client that presents a syntactically invalid "server_name" (SNI) value.</summary>
    [Fact]
    public async Task RawConnection_InvalidServerNameExtension_ServerRejectsHandshake()
    {
        (X509Certificate2 serverCertificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(server.LocalEndPoint!.Address, server.LocalEndPoint!.Port);

        Org.BouncyCastle.Tls.TlsClientProtocol protocol = new(tcpClient.GetStream());

        Assert.Throws<Org.BouncyCastle.Tls.TlsFatalAlertReceived>(() => protocol.Connect(new InvalidSniTlsClient()));
    }

    /// <summary>The server rejects a TLS handshake from a client that presents a "server_name" (SNI) value longer than 255 bytes.</summary>
    [Fact]
    public async Task RawConnection_OverlongServerNameExtension_ServerRejectsHandshake()
    {
        (X509Certificate2 serverCertificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(server.LocalEndPoint!.Address, server.LocalEndPoint!.Port);

        Org.BouncyCastle.Tls.TlsClientProtocol protocol = new(tcpClient.GetStream());

        Assert.Throws<Org.BouncyCastle.Tls.TlsFatalAlertReceived>(() => protocol.Connect(new OverlongSniTlsClient()));
    }

    /// <summary>With <see cref="MsmtHostOptions.RequireFullyQualifiedHostname"/> at its default of <see langword="true"/>, the server rejects a TLS handshake from a client that presents an IP address literal as its "server_name" (SNI) value.</summary>
    [Fact]
    public async Task RawConnection_IpLiteralServerNameWithFullyQualifiedHostnameRequired_ServerRejectsHandshake()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(server.LocalEndPoint!.Address, server.LocalEndPoint!.Port);

        Org.BouncyCastle.Tls.TlsClientProtocol protocol = new(tcpClient.GetStream());
        MsmtConnectOptions clientOptions = new()
        {
            Target = new MsmtNameTarget { Host = "127.0.0.1", Port = server.LocalEndPoint!.Port, ServerName = "127.0.0.1" },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        };

        Assert.Throws<Org.BouncyCastle.Tls.TlsFatalAlertReceived>(() => protocol.Connect(new MsmtTlsClient(clientOptions)));
    }

    /// <summary>With <see cref="MsmtHostOptions.RequireFullyQualifiedHostname"/> disabled, the server accepts a TLS handshake from a client that presents an IP address literal as its "server_name" (SNI) value.</summary>
    [Fact]
    public async Task RawConnection_IpLiteralServerNameWithFullyQualifiedHostnameNotRequired_ServerAcceptsHandshake()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(server.LocalEndPoint!.Address, server.LocalEndPoint!.Port);

        Org.BouncyCastle.Tls.TlsClientProtocol protocol = new(tcpClient.GetStream());
        MsmtConnectOptions clientOptions = new()
        {
            Target = new MsmtNameTarget { Host = "127.0.0.1", Port = server.LocalEndPoint!.Port, ServerName = "127.0.0.1" },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        };

        protocol.Connect(new MsmtTlsClient(clientOptions));
        protocol.Close();
    }

    /// <summary><see cref="MsmtServer.DisposeAsync"/> completes promptly even while a client connection is still open but idle, blocked reading the next message - it must forcibly close that connection's socket rather than rely solely on cancelling it, since the underlying TLS stream does not honor cancellation of an in-flight read.</summary>
    [Fact]
    public async Task DisposeAsync_ClientConnectedButIdle_CompletesPromptly()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(server.LocalEndPoint!.Address, server.LocalEndPoint!.Port);

        Org.BouncyCastle.Tls.TlsClientProtocol protocol = new(tcpClient.GetStream());
        MsmtConnectOptions clientOptions = new()
        {
            Target = new MsmtNameTarget { Host = "127.0.0.1", Port = server.LocalEndPoint!.Port, ServerName = "127.0.0.1" },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        };

        protocol.Connect(new MsmtTlsClient(clientOptions));

        // The client deliberately never sends a message or closes its socket, leaving the server's
        // HandleConnection loop blocked reading the next one.
        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>The client rejects a TLS handshake when the server's certificate does not match the target's <see cref="MsmtNameTarget.ServerName"/>, even though it chains to a trusted authority.</summary>
    [Fact]
    public async Task RawConnection_ServerNameNotOnServerCertificate_ClientRejectsHandshake()
    {
        (X509Certificate2 serverCertificate, X509Certificate2 clientCertificate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        await using MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            RequireFullyQualifiedHostname = false,
            Port = 0,
            Credentials = new MsmtCredentials { Identity = serverCertificate, TrustedAuthorities = trustedAuthorities },
        });

        using TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(server.LocalEndPoint!.Address, server.LocalEndPoint!.Port);

        Org.BouncyCastle.Tls.TlsClientProtocol protocol = new(tcpClient.GetStream());
        MsmtConnectOptions clientOptions = new()
        {
            Target = new MsmtNameTarget { Host = "127.0.0.1", Port = server.LocalEndPoint!.Port, ServerName = "impostor.example.com" },
            Credentials = new MsmtCredentials { Identity = clientCertificate, TrustedAuthorities = trustedAuthorities },
        };

        Assert.Throws<Org.BouncyCastle.Tls.TlsFatalAlert>(() => protocol.Connect(new MsmtTlsClient(clientOptions)));
    }

    private static async Task<MsmtResponse> SendAndWaitForResponse(MsmtClient client, ReadOnlyMemory<byte> payload, MsmtSendOptions? options = null) =>
        await client.Request(payload, options).WaitAsync(TimeSpan.FromSeconds(5));

    private static async Task SendAndWaitForCompletion(MsmtClient client, ReadOnlyMemory<byte> payload, MsmtSendOptions? options = null)
    {
        object tag = new();
        TaskCompletionSource completed = new();
        void Handler(object? sender, MsmtPackageChangedEventArgs args)
        {
            if (ReferenceEquals(args.Package.Tag, tag) && args.Status == MsmtSendStatus.Completed)
            {
                completed.TrySetResult();
            }
        }

        client.PackageChanged += Handler;
        try
        {
            client.Send(payload, (options ?? new MsmtSendOptions()) with { Tag = tag });
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            client.PackageChanged -= Handler;
        }
    }

    private static async Task<MsmtUnlinkedEventArgs> SendAndWaitForDisconnection(MsmtClient client, ReadOnlyMemory<byte> payload, MsmtSendOptions? options = null)
    {
        TaskCompletionSource<MsmtUnlinkedEventArgs> source = new();
        void Handler(object? sender, MsmtUnlinkedEventArgs args) => source.TrySetResult(args);

        client.Unlinked += Handler;
        try
        {
            client.Send(payload, options);
            return await source.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            client.Unlinked -= Handler;
        }
    }

    private static async Task<MsmtLinkFailedEventArgs> SendAndWaitForConnectionFailure(MsmtClient client, ReadOnlyMemory<byte> payload, MsmtSendOptions? options = null)
    {
        TaskCompletionSource<MsmtLinkFailedEventArgs> source = new();
        void Handler(object? sender, MsmtLinkFailedEventArgs args) => source.TrySetResult(args);

        client.LinkFailed += Handler;
        try
        {
            client.Send(payload, options);
            return await source.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            client.LinkFailed -= Handler;
        }
    }

    private sealed class NoSniTlsClient() : Org.BouncyCastle.Tls.DefaultTlsClient(MsmtBcCryptography.Crypto)
    {
        protected override Org.BouncyCastle.Tls.ProtocolVersion[] GetSupportedVersions() => Org.BouncyCastle.Tls.ProtocolVersion.TLSv13.Only();

        protected override int[] GetSupportedCipherSuites() =>
            TlsUtilities.GetSupportedCipherSuites(Crypto, [.. MsmtBcCryptography.CipherSuites]);

        public override Org.BouncyCastle.Tls.TlsAuthentication GetAuthentication() => new NoOpAuthentication();

        private sealed class NoOpAuthentication : Org.BouncyCastle.Tls.TlsAuthentication
        {
            public void NotifyServerCertificate(Org.BouncyCastle.Tls.TlsServerCertificate serverCertificate)
            {
            }

            public Org.BouncyCastle.Tls.TlsCredentials GetClientCredentials(Org.BouncyCastle.Tls.CertificateRequest certificateRequest) => null!;
        }
    }

    private sealed class InvalidSniTlsClient() : Org.BouncyCastle.Tls.DefaultTlsClient(MsmtBcCryptography.Crypto)
    {
        protected override Org.BouncyCastle.Tls.ProtocolVersion[] GetSupportedVersions() => Org.BouncyCastle.Tls.ProtocolVersion.TLSv13.Only();

        protected override int[] GetSupportedCipherSuites() =>
            TlsUtilities.GetSupportedCipherSuites(Crypto, [.. MsmtBcCryptography.CipherSuites]);

        protected override IList<Org.BouncyCastle.Tls.ServerName> GetSniServerNames() =>
            [new Org.BouncyCastle.Tls.ServerName(Org.BouncyCastle.Tls.NameType.host_name, "not a valid host!"u8.ToArray())];

        public override Org.BouncyCastle.Tls.TlsAuthentication GetAuthentication() => new NoOpAuthentication();

        private sealed class NoOpAuthentication : Org.BouncyCastle.Tls.TlsAuthentication
        {
            public void NotifyServerCertificate(Org.BouncyCastle.Tls.TlsServerCertificate serverCertificate)
            {
            }

            public Org.BouncyCastle.Tls.TlsCredentials GetClientCredentials(Org.BouncyCastle.Tls.CertificateRequest certificateRequest) => null!;
        }
    }

    private sealed class OverlongSniTlsClient() : Org.BouncyCastle.Tls.DefaultTlsClient(MsmtBcCryptography.Crypto)
    {
        protected override Org.BouncyCastle.Tls.ProtocolVersion[] GetSupportedVersions() => Org.BouncyCastle.Tls.ProtocolVersion.TLSv13.Only();

        protected override int[] GetSupportedCipherSuites() =>
            TlsUtilities.GetSupportedCipherSuites(Crypto, [.. MsmtBcCryptography.CipherSuites]);

        protected override IList<Org.BouncyCastle.Tls.ServerName> GetSniServerNames() =>
            [new Org.BouncyCastle.Tls.ServerName(Org.BouncyCastle.Tls.NameType.host_name, Encoding.ASCII.GetBytes(new string('a', 300)))];

        public override Org.BouncyCastle.Tls.TlsAuthentication GetAuthentication() => new NoOpAuthentication();

        private sealed class NoOpAuthentication : Org.BouncyCastle.Tls.TlsAuthentication
        {
            public void NotifyServerCertificate(Org.BouncyCastle.Tls.TlsServerCertificate serverCertificate)
            {
            }

            public Org.BouncyCastle.Tls.TlsCredentials GetClientCredentials(Org.BouncyCastle.Tls.CertificateRequest certificateRequest) => null!;
        }
    }

    private static async Task<(MsmtHeader Header, byte[] Payload)> ExchangeRawAsync(Stream stream, MsmtHeader header, byte[] payload)
    {
        byte[] headerBuffer = new byte[MsmtHeader.Size];
        header.Write(headerBuffer);
        await stream.WriteAsync(headerBuffer);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload);
        }

        await MsmtProtocol.ReadExact(stream, headerBuffer, CancellationToken.None);
        MsmtHeader responseHeader = MsmtHeader.Read(headerBuffer);

        byte[] responsePayload = [];
        if (responseHeader.Length > 0)
        {
            responsePayload = new byte[responseHeader.Length];
            await MsmtProtocol.ReadExact(stream, responsePayload, CancellationToken.None);
        }

        return (responseHeader, responsePayload);
    }
}
