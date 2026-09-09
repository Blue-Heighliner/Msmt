namespace BlueHeighliner.Msmt.Tests.Unit;

/// <summary>Unit tests for <see cref="MsmtSendOptions"/>.</summary>
public sealed class MsmtSendOptionsTests
{
    /// <summary>Setting <see cref="MsmtSendOptions.Dscp"/> to <see langword="null"/> leaves it unset.</summary>
    [Fact]
    public void Dscp_Null_LeavesItUnset()
    {
        MsmtSendOptions options = new() { Dscp = null };

        Assert.Null(options.Dscp);
    }

    /// <summary>Setting <see cref="MsmtSendOptions.Dscp"/> to a value within its valid 6-bit range (0-63) succeeds.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(46)]
    [InlineData(63)]
    public void Dscp_WithinValidRange_Succeeds(int dscp)
    {
        MsmtSendOptions options = new() { Dscp = dscp };

        Assert.Equal(dscp, options.Dscp);
    }

    /// <summary>Setting <see cref="MsmtSendOptions.Dscp"/> below 0 throws.</summary>
    [Fact]
    public void Dscp_BelowZero_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MsmtSendOptions { Dscp = -1 });

    /// <summary>Setting <see cref="MsmtSendOptions.Dscp"/> above 63 throws.</summary>
    [Fact]
    public void Dscp_Above63_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MsmtSendOptions { Dscp = 64 });
}
