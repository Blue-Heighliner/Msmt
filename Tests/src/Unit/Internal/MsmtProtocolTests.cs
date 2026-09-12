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

    /// <summary><see cref="MsmtProtocol.ClonePooled"/> copies the source into a new, independent pooled buffer.</summary>
    [Fact]
    public void ClonePooled_NonEmptySource_CopiesIntoIndependentBuffer()
    {
        byte[] source = [1, 2, 3, 4];

        using IMemoryOwner<byte> clone = MsmtProtocol.ClonePooled(source);

        Assert.Equal(source, clone.Memory.ToArray());

        source[0] = 99;
        Assert.Equal(1, clone.Memory.Span[0]);
    }

    /// <summary><see cref="MsmtProtocol.ClonePooled"/> handles an empty source, returning an empty buffer.</summary>
    [Fact]
    public void ClonePooled_EmptySource_ReturnsEmptyBuffer()
    {
        using IMemoryOwner<byte> clone = MsmtProtocol.ClonePooled(ReadOnlyMemory<byte>.Empty);

        Assert.True(clone.Memory.IsEmpty);
    }

    /// <summary>A duration within <see cref="MsmtProtocol.MaxTimerDuration"/> passes through unchanged.</summary>
    [Fact]
    public void ClampToMaxTimerDuration_WithinLimit_ReturnsUnchanged()
    {
        TimeSpan duration = TimeSpan.FromSeconds(5);

        Assert.Equal(duration, MsmtProtocol.ClampToMaxTimerDuration(duration));
    }

    /// <summary>A duration beyond <see cref="MsmtProtocol.MaxTimerDuration"/> is clamped down to it.</summary>
    [Fact]
    public void ClampToMaxTimerDuration_BeyondLimit_ClampsToMaxTimerDuration()
    {
        Assert.Equal(MsmtProtocol.MaxTimerDuration, MsmtProtocol.ClampToMaxTimerDuration(TimeSpan.MaxValue));
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
}
