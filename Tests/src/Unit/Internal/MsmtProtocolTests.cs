namespace BlueHeighliner.Msmt.Tests.Unit.Internal;

/// <summary>Unit tests for <see cref="MsmtProtocol"/>.</summary>
public sealed class MsmtProtocolTests
{
    /// <summary>An IP address literal is parsed directly, without a DNS lookup.</summary>
    [Fact]
    public void ResolveAddress_IPAddressLiteral_ParsesDirectly()
    {
        IPAddress address = MsmtProtocol.ResolveAddress("127.0.0.1");

        Assert.Equal(IPAddress.Parse("127.0.0.1"), address);
    }

    /// <summary>A DNS hostname is resolved via a DNS lookup.</summary>
    [Fact]
    public void ResolveAddress_DnsHostname_ResolvesViaDns()
    {
        IPAddress address = MsmtProtocol.ResolveAddress("localhost");

        Assert.True(IPAddress.IsLoopback(address));
    }

    /// <summary>A payload within <see cref="MsmtLimits.MaxPayloadLength"/> passes validation without throwing.</summary>
    [Fact]
    public void ValidatePayloadLength_WithinLimit_DoesNotThrow() =>
        Record.Exception(() => MsmtProtocol.ValidatePayloadLength(MsmtLimits.MaxPayloadLength, "payload"));

    /// <summary>A payload longer than <see cref="MsmtLimits.MaxPayloadLength"/> throws, naming the given parameter.</summary>
    [Fact]
    public void ValidatePayloadLength_BeyondLimit_ThrowsWithParameterName()
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => MsmtProtocol.ValidatePayloadLength(MsmtLimits.MaxPayloadLength + 1, "payload"));

        Assert.Equal("payload", exception.ParamName);
    }

    /// <summary>TCP keep-alive is enabled on a socket when a time is given, and left alone when it is <see langword="null"/>.</summary>
    [Fact]
    public void ApplyTcpKeepAlive_TimeGivenOrNull_EnablesOrLeavesKeepAlive()
    {
        using Socket enabled = new(SocketType.Stream, ProtocolType.Tcp);
        using Socket untouched = new(SocketType.Stream, ProtocolType.Tcp);

        MsmtProtocol.ApplyTcpKeepAlive(enabled, TimeSpan.FromSeconds(30));
        MsmtProtocol.ApplyTcpKeepAlive(untouched, null);

        Assert.Equal(1, (int)enabled.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!);
        Assert.Equal(0, (int)untouched.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!);
    }
}
