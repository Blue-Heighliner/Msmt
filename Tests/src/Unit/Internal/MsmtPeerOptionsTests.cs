namespace BlueHeighliner.Msmt.Tests.Unit.Internal;

/// <summary>Unit tests for the option validation <see cref="MsmtPeer"/> performs when constructed.</summary>
public sealed class MsmtPeerOptionsTests
{
    /// <summary>Creates the credentials every test constructs its options with.</summary>
    public MsmtPeerOptionsTests()
    {
        (X509Certificate2 certificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        credentials = new MsmtCredentials { Identity = certificate, TrustedAuthorities = trustedAuthorities };
    }

    private readonly MsmtCredentials credentials;

    /// <summary>A zero or negative timeout or interval is rejected, naming the offending option.</summary>
    [Theory]
    [InlineData(nameof(MsmtOptions.MaxIdleTime))]
    [InlineData(nameof(MsmtOptions.HandshakeTimeout))]
    [InlineData(nameof(MsmtOptions.StallTimeout))]
    [InlineData(nameof(MsmtOptions.ResponseTimeout))]
    [InlineData(nameof(MsmtOptions.TcpKeepAliveTime))]
    [InlineData(nameof(MsmtOptions.KeepAliveMinInterval))]
    [InlineData(nameof(MsmtOptions.KeepAliveMaxInterval))]
    public void Constructor_NonPositiveValue_Throws(string option)
    {
        foreach (TimeSpan invalid in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-1) })
        {
            MsmtOptions options = option switch
            {
                nameof(MsmtOptions.MaxIdleTime) => new MsmtOptions { Credentials = credentials, MaxIdleTime = invalid },
                nameof(MsmtOptions.HandshakeTimeout) => new MsmtOptions { Credentials = credentials, HandshakeTimeout = invalid },
                nameof(MsmtOptions.StallTimeout) => new MsmtOptions { Credentials = credentials, StallTimeout = invalid },
                nameof(MsmtOptions.ResponseTimeout) => new MsmtOptions { Credentials = credentials, ResponseTimeout = invalid },
                nameof(MsmtOptions.TcpKeepAliveTime) => new MsmtOptions { Credentials = credentials, TcpKeepAliveTime = invalid },
                nameof(MsmtOptions.KeepAliveMinInterval) => new MsmtOptions { Credentials = credentials, KeepAliveMinInterval = invalid, KeepAliveMaxInterval = TimeSpan.FromMinutes(5) },
                _ => new MsmtOptions { Credentials = credentials, KeepAliveMaxInterval = invalid, KeepAliveMinInterval = TimeSpan.FromSeconds(1) },
            };

            ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() => new MsmtPeer(options));
            Assert.Contains(option, exception.Message);
        }
    }

    /// <summary>A keep-alive minimum above the maximum is rejected.</summary>
    [Fact]
    public void Constructor_KeepAliveMinAboveMax_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MsmtPeer(new MsmtOptions { Credentials = credentials, KeepAliveMinInterval = TimeSpan.FromMinutes(6), KeepAliveMaxInterval = TimeSpan.FromMinutes(5) }));

    /// <summary>Every nullable timeout may be disabled with <see langword="null"/>, and equal keep-alive bounds are allowed.</summary>
    [Fact]
    public async Task Constructor_DisabledTimeoutsAndEqualKeepAliveBounds_Accepted()
    {
        await using MsmtPeer peer = new(new MsmtOptions
        {
            Credentials = credentials,
            MaxIdleTime = null,
            HandshakeTimeout = null,
            StallTimeout = null,
            ResponseTimeout = null,
            TcpKeepAliveTime = null,
            KeepAliveMinInterval = TimeSpan.FromMinutes(4),
            KeepAliveMaxInterval = TimeSpan.FromMinutes(4),
        });

        Assert.Empty(peer.ActiveConnections);
    }
}
