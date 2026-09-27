namespace BlueHeighliner.Msmt.Tests.Unit;

/// <summary>Unit tests for <see cref="IMsmtSessionPeer.Factory"/>.</summary>
public sealed class MsmtSessionPeerFactoryTests
{
    /// <summary><see cref="IMsmtSessionPeer.Factory.Create"/> returns a new, independent, not yet listening peer with no connections.</summary>
    [Fact]
    public async Task Create_ValidOptions_ReturnsIndependentPeerNotListeningWithoutConnections()
    {
        (X509Certificate2 certificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        MsmtCredentials credentials = new() { Identity = certificate, TrustedAuthorities = trustedAuthorities };
        IMsmtSessionPeer.Factory factory = new();

        await using IMsmtSessionPeer first = factory.Create(new MsmtSessionPeerOptions { Credentials = credentials });
        await using IMsmtSessionPeer second = factory.Create(new MsmtSessionPeerOptions { Credentials = credentials });

        Assert.IsType<MsmtSessionPeer>(first);
        Assert.NotSame(first, second);
        Assert.False(first.IsListening);
        Assert.Empty(first.Connections);
    }
}
