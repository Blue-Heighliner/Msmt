namespace BlueHeighliner.Msmt.Tests.Integration;

/// <summary>Integration tests for how connections are timed out, kept alive, bounded in lifetime, and evicted.</summary>
public sealed class MsmtConnectionLifecycleTests
{
    private readonly TimeSpan waitLimit = TimeSpan.FromSeconds(10);

    /// <summary>A client that connects but never starts a TLS handshake is dropped after the handshake timeout, reported as a timed out link failure.</summary>
    [Fact]
    public async Task Listener_ClientNeverHandshakes_LinkFailsWithTimeout()
    {
        (_, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtPeer peerB = new(ListenerOptions(certificateB, trustedAuthorities) with { HandshakeTimeout = TimeSpan.FromMilliseconds(300) });
        Task<MsmtLinkFailedEventArgs> failed = WaitFor(peerB.LinkFailed);
        peerB.StartListener(0, "127.0.0.1");

        using TcpClient silent = new();
        await silent.ConnectAsync(IPAddress.Loopback, peerB.Listener!.Port);

        Assert.IsType<TimeoutException>((await failed).Exception);
    }

    /// <summary>A client that completes its handshake but never sends a first message is dropped after the handshake timeout.</summary>
    [Fact]
    public async Task Listener_ClientNeverSendsFirstMessage_UnlinksWithTimeout()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtPeer peerB = new(ListenerOptions(certificateB, trustedAuthorities) with { HandshakeTimeout = TimeSpan.FromMilliseconds(1500) });
        Task<MsmtUnlinkedEventArgs> unlinked = WaitFor(peerB.Unlinked);
        peerB.StartListener(0, "127.0.0.1");

        (TcpClient tcpClient, _) = await ConnectRaw(peerB.Listener!, certificateA, trustedAuthorities);
        using (tcpClient)
        {
            Assert.IsType<TimeoutException>((await unlinked).Exception);
        }
    }

    /// <summary>A client that stalls part way through a message header or payload is dropped after the stall timeout.</summary>
    [Theory]
    [InlineData(5)]
    [InlineData(20)]
    public async Task Listener_ClientStallsMidMessage_UnlinksWithTimeout(int bytesSent)
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtPeer peerB = new(ListenerOptions(certificateB, trustedAuthorities) with { StallTimeout = TimeSpan.FromMilliseconds(300) });
        Task<MsmtUnlinkedEventArgs> unlinked = WaitFor(peerB.Unlinked);
        peerB.StartListener(0, "127.0.0.1");

        (TcpClient tcpClient, RekeyableTlsClientProtocol protocol) = await ConnectRaw(peerB.Listener!, certificateA, trustedAuthorities);
        using (tcpClient)
        {
            byte[] message = new byte[MsmtHeader.Size + 100];
            new MsmtHeader { Version = MsmtHeader.SupportedVersion, Flags = MsmtMessageFlags.None, MessageId = 1, Length = 100 }.Write(message);
            await protocol.Stream.WriteAsync(message.AsMemory(0, bytesSent));

            Assert.IsType<TimeoutException>((await unlinked).Exception);
        }
    }

    /// <summary>A remote peer that accepts the connection but never answers the TLS handshake fails the send with a timeout, after reporting the link failure.</summary>
    [Fact]
    public async Task Request_RemoteNeverAnswersHandshake_FailsWithTimeout()
    {
        (X509Certificate2 certificateA, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        using TcpListener silent = new(IPAddress.Loopback, 0);
        silent.Start();
        await using MsmtPeer peerA = new(SenderOptions(certificateA, trustedAuthorities) with { HandshakeTimeout = TimeSpan.FromMilliseconds(300) });
        Task<MsmtLinkFailedEventArgs> failed = WaitFor(peerA.LinkFailed);
        MsmtTarget target = new() { Host = "127.0.0.1", Port = ((IPEndPoint)silent.LocalEndpoint).Port };

        await Assert.ThrowsAsync<TimeoutException>(() => peerA.Request(target, "hello"u8.ToArray()).WaitAsync(waitLimit));

        Assert.IsType<TimeoutException>((await failed).Exception);
    }

    /// <summary><see cref="MsmtPeer.Test"/> gives up on a remote peer that never answers the handshake once the handshake timeout passes.</summary>
    [Fact]
    public async Task Test_RemoteNeverAnswersHandshake_FailsWithTimeout()
    {
        (X509Certificate2 certificateA, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        using TcpListener silent = new(IPAddress.Loopback, 0);
        silent.Start();
        await using MsmtPeer peerA = new(SenderOptions(certificateA, trustedAuthorities) with { HandshakeTimeout = TimeSpan.FromMilliseconds(300) });

        await Assert.ThrowsAsync<TimeoutException>(() => peerA.Test(new MsmtTarget { Host = "127.0.0.1", Port = ((IPEndPoint)silent.LocalEndpoint).Port }).WaitAsync(waitLimit));
    }

    /// <summary>A message the remote application never acknowledges fails with a timeout once the response timeout passes, without blocking later sends.</summary>
    [Fact]
    public async Task Request_NeverAcknowledged_FailsWithTimeoutAndLaterSendsStillWork()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtPeer peerB = new(ListenerOptions(certificateB, trustedAuthorities));
        await using MsmtPeer peerA = new(SenderOptions(certificateA, trustedAuthorities) with { ResponseTimeout = TimeSpan.FromMilliseconds(500) });

        int received = 0;
        peerB.Received.Subscribe(args =>
        {
            if (Interlocked.Increment(ref received) == 1)
            {
                args.Responder.Defer();
            }
        });
        peerB.StartListener(0, "127.0.0.1");
        Task<MsmtUnlinkedEventArgs> unlinked = WaitFor(peerA.Unlinked);

        await Assert.ThrowsAsync<TimeoutException>(() => peerA.Request(peerB.Listener!, "first"u8.ToArray()).WaitAsync(waitLimit));
        Assert.IsType<TimeoutException>((await unlinked).Exception);

        MsmtResponse second = await peerA.Request(peerB.Listener!, "second"u8.ToArray()).WaitAsync(waitLimit);
        Assert.True(second.Success);
    }

    /// <summary>A connection the remote peer closes while it sits idle is noticed and reported promptly, and the next send transparently opens a new one.</summary>
    [Fact]
    public async Task Session_RemoteClosesIdleConnection_UnlinksPromptlyAndNextSendReconnects()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtPeer peerB = new(ListenerOptions(certificateB, trustedAuthorities) with { MaxIdleTime = TimeSpan.FromMilliseconds(500) });
        await using MsmtPeer peerA = new(SenderOptions(certificateA, trustedAuthorities) with { Mode = MsmtOperationMode.Session, MaxIdleTime = null });
        peerB.StartListener(0, "127.0.0.1");
        Task<MsmtUnlinkedEventArgs> unlinked = WaitFor(peerA.Unlinked);

        Assert.True((await peerA.Request(peerB.Listener!, "one"u8.ToArray()).WaitAsync(waitLimit)).Success);

        Assert.NotNull((await unlinked).Exception);
        Assert.True((await peerA.Request(peerB.Listener!, "two"u8.ToArray()).WaitAsync(waitLimit)).Success);
    }

    /// <summary>A Session connection is closed by the client when its negotiated lifetime ends, even with no further traffic.</summary>
    [Fact]
    public async Task Session_LifetimeEnds_ClientClosesConnectionWithoutTraffic()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtPeer peerB = new(ListenerOptions(certificateB, trustedAuthorities));
        await using MsmtPeer peerA = new(SenderOptions(certificateA, trustedAuthorities) with { Mode = MsmtOperationMode.Session, SessionLifetime = TimeSpan.FromSeconds(1), MaxIdleTime = null });
        peerB.StartListener(0, "127.0.0.1");
        Task<MsmtUnlinkedEventArgs> unlinked = WaitFor(peerA.Unlinked);

        Assert.True((await peerA.Request(peerB.Listener!, "hello"u8.ToArray()).WaitAsync(waitLimit)).Success);

        Assert.Null((await unlinked).Exception);
    }

    /// <summary>A server closes a session that goes silent once its negotiated lifetime, plus a short grace period, has ended.</summary>
    [Fact]
    public async Task Listener_SessionClientGoesSilent_ClosesAtLifetimeEnd()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtPeer peerB = new(ListenerOptions(certificateB, trustedAuthorities) with { MaxIdleTime = null });
        Task<MsmtUnlinkedEventArgs> unlinked = WaitFor(peerB.Unlinked);
        peerB.StartListener(0, "127.0.0.1");

        (TcpClient tcpClient, RekeyableTlsClientProtocol protocol) = await ConnectRaw(peerB.Listener!, certificateA, trustedAuthorities);
        using (tcpClient)
        {
            byte[] lifetime = "1"u8.ToArray();
            MsmtHeader negotiation = new() { Version = MsmtHeader.SupportedVersion, Flags = MsmtMessageFlags.SessionModeNegotiation, MessageId = 1, Length = (uint)lifetime.Length };
            (MsmtHeader response, _) = await ExchangeRaw(protocol.Stream, negotiation, lifetime);
            Assert.Equal(MsmtMessageFlags.SessionModeAccepted, response.Flags);

            Assert.Null((await unlinked).Exception);
        }
    }

    /// <summary>A listener closes a connection that did not negotiate a session after serving as many messages as its rekey limit allows.</summary>
    [Fact]
    public async Task Listener_RekeyLimitReached_ClosesConnection()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtPeer peerB = new(ListenerOptions(certificateB, trustedAuthorities) with { RekeyLimit = 2 });
        Task<MsmtUnlinkedEventArgs> unlinked = WaitFor(peerB.Unlinked);
        peerB.StartListener(0, "127.0.0.1");

        (TcpClient tcpClient, RekeyableTlsClientProtocol protocol) = await ConnectRaw(peerB.Listener!, certificateA, trustedAuthorities);
        using (tcpClient)
        {
            for (ushort id = 1; id <= 2; id++)
            {
                (MsmtHeader response, _) = await ExchangeRaw(protocol.Stream, new MsmtHeader { Version = MsmtHeader.SupportedVersion, Flags = MsmtMessageFlags.None, MessageId = id, Length = 0 }, []);
                Assert.True((response.Flags & MsmtMessageFlags.MessageSuccess) != 0);
            }

            Assert.Null((await unlinked).Exception);
            await Assert.ThrowsAnyAsync<Exception>(() => ExchangeRaw(protocol.Stream, new MsmtHeader { Version = MsmtHeader.SupportedVersion, Flags = MsmtMessageFlags.None, MessageId = 3, Length = 0 }, []));
        }
    }

    /// <summary>Keep-alives do not count as application traffic, so a Session connection kept alive on the wire is still evicted once no message uses it.</summary>
    [Fact]
    public async Task Session_OnlyKeepAlivesFlow_StillEvictedAfterMaxIdleTime()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtPeer peerB = new(ListenerOptions(certificateB, trustedAuthorities) with { MaxIdleTime = null });
        await using MsmtPeer peerA = new(SenderOptions(certificateA, trustedAuthorities) with
        {
            Mode = MsmtOperationMode.Session,
            SessionLifetime = TimeSpan.FromMinutes(5),
            KeepAliveMinInterval = TimeSpan.FromMilliseconds(200),
            KeepAliveMaxInterval = TimeSpan.FromMilliseconds(200),
            MaxIdleTime = TimeSpan.FromMilliseconds(2500),
        });
        peerB.StartListener(0, "127.0.0.1");
        Task<MsmtDisconnectedEventArgs> disconnected = WaitFor(peerA.Disconnected);

        await peerA.Request(peerB.Listener!, "hello"u8.ToArray()).WaitAsync(waitLimit);

        await disconnected;
        Assert.Equal(0, peerA.SenderCount);
    }

    /// <summary>A listener evicts a connection whose client only sends reachability checks, since those are not application traffic.</summary>
    [Fact]
    public async Task Listener_ClientOnlySendsReachabilityChecks_EvictsAsIdleWithTimeout()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtPeer peerB = new(ListenerOptions(certificateB, trustedAuthorities) with { MaxIdleTime = TimeSpan.FromSeconds(1), RekeyLimit = 1000 });
        Task<MsmtUnlinkedEventArgs> unlinked = WaitFor(peerB.Unlinked);
        peerB.StartListener(0, "127.0.0.1");

        (TcpClient tcpClient, RekeyableTlsClientProtocol protocol) = await ConnectRaw(peerB.Listener!, certificateA, trustedAuthorities);
        using (tcpClient)
        {
            Task pinger = Task.Run(async () =>
            {
                try
                {
                    for (ushort id = 1; ; id++)
                    {
                        await ExchangeRaw(protocol.Stream, new MsmtHeader { Version = MsmtHeader.SupportedVersion, Flags = MsmtMessageFlags.ReachabilityCheck, MessageId = id, Length = 0 }, []);
                        await Task.Delay(200);
                    }
                }
                catch (Exception)
                {
                }
            });

            Assert.IsType<TimeoutException>((await unlinked).Exception);
            await pinger.WaitAsync(waitLimit);
        }
    }

    /// <summary>A pooled connection idle in Message Mode, with no open socket at all, is still evicted, and its tagged send stays observable afterwards.</summary>
    [Fact]
    public async Task Send_TaggedSendThroughEvictedEntry_PackageStaysObservable()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtPeer peerB = new(ListenerOptions(certificateB, trustedAuthorities));
        await using MsmtPeer peerA = new(SenderOptions(certificateA, trustedAuthorities) with { MaxIdleTime = TimeSpan.FromMilliseconds(300) });
        peerB.StartListener(0, "127.0.0.1");

        object tag = new();
        Task<MsmtPackageChangedEventArgs> completed = WaitFor(peerA.PackageChanged, args => args.Status == MsmtSendStatus.Completed);
        peerA.Send(peerB.Listener!, "hello"u8.ToArray(), new MsmtSendOptions { Tag = tag });
        await completed;

        using CancellationTokenSource timeout = new(waitLimit);
        while (peerA.SenderCount > 0)
        {
            await Task.Delay(50, timeout.Token);
        }

        IMsmtPackage package = peerA.GetPackage(tag)!;
        Assert.NotNull(package);
        Assert.Equal(MsmtSendStatus.Completed, package.Status);
        Assert.Empty(peerA.Packages);
        package.Cancel();
        Assert.Equal(MsmtSendStatus.Completed, package.Status);
    }

    /// <summary>A keep-alive keeps the connection alive on the wire without counting as application traffic.</summary>
    [Fact]
    public async Task Session_Idle_SendsKeepAliveWithoutCountingAsActivity()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtPeer peerB = new(ListenerOptions(certificateB, trustedAuthorities) with { MaxIdleTime = null });
        peerB.StartListener(0, "127.0.0.1");

        await using MsmtClient client = new();
        await client.Connect(new MsmtConnectOptions
        {
            Target = peerB.Listener!,
            Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.Session,
            SessionLifetime = TimeSpan.FromMinutes(5),
            KeepAliveMinInterval = TimeSpan.FromMilliseconds(300),
            KeepAliveMaxInterval = TimeSpan.FromMilliseconds(300),
        });

        await client.Request("hello"u8.ToArray()).WaitAsync(waitLimit);
        DateTime activityAt = client.LastActivityUtc;

        using CancellationTokenSource timeout = new(waitLimit);
        while (client.LastWireActivityUtc < activityAt + TimeSpan.FromMilliseconds(300))
        {
            await Task.Delay(50, timeout.Token);
        }

        Assert.Equal(activityAt, client.LastActivityUtc);
        Assert.True(client.IsConnected);
    }

    /// <summary>TCP keep-alive is enabled on a client's socket unless <see cref="MsmtOptions.TcpKeepAliveTime"/> is <see langword="null"/>.</summary>
    [Theory]
    [InlineData(60, 1)]
    [InlineData(null, 0)]
    public async Task Client_TcpKeepAliveTime_ControlsSocketOption(int? seconds, int expectedOption)
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtPeer peerB = new(ListenerOptions(certificateB, trustedAuthorities));
        peerB.StartListener(0, "127.0.0.1");

        await using MsmtClient client = new();
        await client.Connect(new MsmtConnectOptions
        {
            Target = peerB.Listener!,
            Credentials = new MsmtCredentials { Identity = certificateA, TrustedAuthorities = trustedAuthorities },
            Mode = MsmtOperationMode.Session,
            TcpKeepAliveTime = seconds is { } value ? TimeSpan.FromSeconds(value) : null,
        });

        await client.Request("hello"u8.ToArray()).WaitAsync(waitLimit);

        Assert.Equal(expectedOption, (int)client.Socket!.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!);
    }

    private MsmtOptions ListenerOptions(X509Certificate2 certificate, X509Certificate2Collection trustedAuthorities) => new()
    {
        Credentials = new MsmtCredentials { Identity = certificate, TrustedAuthorities = trustedAuthorities },
        RequireFullyQualifiedHostname = false,
    };

    private MsmtOptions SenderOptions(X509Certificate2 certificate, X509Certificate2Collection trustedAuthorities) => new()
    {
        Credentials = new MsmtCredentials { Identity = certificate, TrustedAuthorities = trustedAuthorities },
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

    private async Task<(TcpClient Client, RekeyableTlsClientProtocol Protocol)> ConnectRaw(MsmtTarget listener, X509Certificate2 certificate, X509Certificate2Collection trustedAuthorities)
    {
        TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(listener.Host, listener.Port);

        MsmtConnectOptions connectOptions = new()
        {
            Target = listener,
            Credentials = new MsmtCredentials { Identity = certificate, TrustedAuthorities = trustedAuthorities },
        };

        RekeyableTlsClientProtocol protocol = new(tcpClient.GetStream());
        await Task.Run(() => protocol.Connect(new MsmtTlsClient(connectOptions)));
        return (tcpClient, protocol);
    }

    private async Task<(MsmtHeader Header, byte[] Payload)> ExchangeRaw(Stream stream, MsmtHeader header, byte[] payload)
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

        byte[] responsePayload = new byte[responseHeader.Length];
        await MsmtProtocol.ReadExact(stream, responsePayload, CancellationToken.None);
        return (responseHeader, responsePayload);
    }
}
