namespace BlueHeighliner.Msmt.Tests.Integration;

/// <summary>Integration tests for <see cref="MsmtReachabilityChecker"/>.</summary>
public sealed class MsmtReachabilityCheckerTests
{
    private readonly TimeSpan waitLimit = TimeSpan.FromSeconds(10);

    /// <summary>Checking a reachable peer's listener reports it reachable without delivering anything to its application.</summary>
    [Fact]
    public async Task Reach_ReachablePeer_ReturnsTrueWithoutDelivering()
    {
        (X509Certificate2 certificateA, X509Certificate2 certificateB, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        await using MsmtMessagePeer peerB = Listener(certificateB, trustedAuthorities);
        int received = 0;
        peerB.Receiver = (source, identity, payload, responder) =>
        {
            payload.Dispose();
            Interlocked.Increment(ref received);
            responder!.Accept();
        };
        peerB.StartListener(0, "127.0.0.1");
        IMsmtReachabilityChecker checker = new MsmtReachabilityChecker();

        Assert.True(await checker.Reach(Target(peerB), Options(certificateA, trustedAuthorities)).WaitAsync(waitLimit));

        Assert.Equal(0, received);
    }

    /// <summary>Cancelling a check honors the token, even mid-handshake against a peer that never answers.</summary>
    [Fact]
    public async Task Reach_Cancelled_ThrowsOperationCanceled()
    {
        (X509Certificate2 certificateA, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        IMsmtReachabilityChecker checker = new MsmtReachabilityChecker();
        using TcpListener silent = new(IPAddress.Loopback, 0);
        silent.Start();
        MsmtTarget target = new() { Host = "127.0.0.1", Port = ((IPEndPoint)silent.LocalEndpoint).Port };

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.Reach(target, Options(certificateA, trustedAuthorities), cancelled.Token));
    }

    /// <summary>A remote peer that never answers the handshake fails the check with a timeout.</summary>
    [Fact]
    public async Task Reach_RemoteNeverAnswersHandshake_FailsWithTimeout()
    {
        (X509Certificate2 certificateA, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        using TcpListener silent = new(IPAddress.Loopback, 0);
        silent.Start();
        IMsmtReachabilityChecker checker = new MsmtReachabilityChecker();
        MsmtTarget target = new() { Host = "127.0.0.1", Port = ((IPEndPoint)silent.LocalEndpoint).Port };

        await Assert.ThrowsAsync<TimeoutException>(() => checker.Reach(target, Options(certificateA, trustedAuthorities) with { HandshakeTimeout = TimeSpan.FromMilliseconds(300) }).WaitAsync(waitLimit));
    }

    /// <summary>Options with a non-positive timeout are rejected.</summary>
    [Fact]
    public async Task Reach_NonPositiveTimeout_Throws()
    {
        (X509Certificate2 certificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        IMsmtReachabilityChecker checker = new MsmtReachabilityChecker();
        MsmtTarget target = new() { Host = "127.0.0.1", Port = 1 };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => checker.Reach(target, Options(certificate, trustedAuthorities) with { HandshakeTimeout = TimeSpan.Zero }));
    }

    private MsmtMessagePeer Listener(X509Certificate2 certificate, X509Certificate2Collection trustedAuthorities) =>
        new(new MsmtMessagePeerOptions { Credentials = new MsmtCredentials { Identity = certificate, TrustedAuthorities = trustedAuthorities }, RequireFullyQualifiedHostname = false });

    private MsmtOptions Options(X509Certificate2 certificate, X509Certificate2Collection trustedAuthorities) =>
        new() { Credentials = new MsmtCredentials { Identity = certificate, TrustedAuthorities = trustedAuthorities } };

    private MsmtNameTarget Target(MsmtMessagePeer peer) => new() { Host = peer.Listener!.Host, Port = peer.Listener!.Port, ServerName = peer.Listener!.Host };
}
