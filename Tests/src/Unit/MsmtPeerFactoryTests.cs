namespace BlueHeighliner.Msmt.Tests.Unit;

/// <summary>Unit tests for <see cref="MsmtPeerFactory"/>.</summary>
public sealed class MsmtPeerFactoryTests
{
    /// <summary><see cref="MsmtPeerFactory.Create"/> returns a new, independent <see cref="MsmtPeer"/> for the given options.</summary>
    [Fact]
    public async Task Create_ValidOptions_ReturnsIndependentPeer()
    {
        (X509Certificate2 certificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        MsmtOptions options = new() { Credentials = new MsmtCredentials { Identity = certificate, TrustedAuthorities = trustedAuthorities } };
        MsmtPeerFactory factory = new();

        await using IMsmtPeer first = factory.Create(options);
        await using IMsmtPeer second = factory.Create(options);

        Assert.IsType<MsmtPeer>(first);
        Assert.NotSame(first, second);
    }
}
