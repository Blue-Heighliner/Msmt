namespace BlueHeighliner.Msmt.Tests.Unit;

/// <summary>Unit tests for <see cref="IMsmtMessagePeer.Factory"/>.</summary>
public sealed class MsmtMessagePeerFactoryTests
{
    /// <summary><see cref="IMsmtMessagePeer.Factory.Create"/> returns a new, independent <see cref="MsmtMessagePeer"/> for the given options.</summary>
    [Fact]
    public async Task Create_ValidOptions_ReturnsIndependentPeer()
    {
        (X509Certificate2 certificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        MsmtMessagePeerOptions options = new() { Credentials = new MsmtCredentials { Identity = certificate, TrustedAuthorities = trustedAuthorities } };
        IMsmtMessagePeer.Factory factory = new();

        await using IMsmtMessagePeer first = factory.Create(options);
        await using IMsmtMessagePeer second = factory.Create(options);

        Assert.IsType<MsmtMessagePeer>(first);
        Assert.NotSame(first, second);
    }
}
