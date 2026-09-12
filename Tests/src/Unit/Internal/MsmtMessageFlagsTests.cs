namespace BlueHeighliner.Msmt.Tests.Unit.Internal;

/// <summary>Unit tests for <see cref="MsmtMessageFlags"/>'s composite values, per the ICD's Figure 5-3.</summary>
public sealed class MsmtMessageFlagsTests
{
    /// <summary><see cref="MsmtMessageFlags.SessionModeAccepted"/> combines <see cref="MsmtMessageFlags.SessionModeNegotiation"/> and <see cref="MsmtMessageFlags.MessageSuccess"/>.</summary>
    [Fact]
    public void SessionModeAccepted_IsSessionModeNegotiationAndMessageSuccess() =>
        Assert.Equal(MsmtMessageFlags.SessionModeNegotiation | MsmtMessageFlags.MessageSuccess, MsmtMessageFlags.SessionModeAccepted);

    /// <summary><see cref="MsmtMessageFlags.SessionModeRejected"/> is <see cref="MsmtMessageFlags.SessionModeNegotiation"/> alone, without <see cref="MsmtMessageFlags.MessageSuccess"/>.</summary>
    [Fact]
    public void SessionModeRejected_IsSessionModeNegotiationOnly()
    {
        Assert.Equal(MsmtMessageFlags.SessionModeNegotiation, MsmtMessageFlags.SessionModeRejected);
        Assert.Equal(MsmtMessageFlags.None, MsmtMessageFlags.SessionModeRejected & MsmtMessageFlags.MessageSuccess);
    }

    /// <summary><see cref="MsmtMessageFlags.SessionModeUnsupported"/> combines <see cref="MsmtMessageFlags.SessionModeNegotiation"/> and <see cref="MsmtMessageFlags.InvalidPreambleOrModeUnsupported"/>.</summary>
    [Fact]
    public void SessionModeUnsupported_IsSessionModeNegotiationAndInvalidPreamble() =>
        Assert.Equal(MsmtMessageFlags.SessionModeNegotiation | MsmtMessageFlags.InvalidPreambleOrModeUnsupported, MsmtMessageFlags.SessionModeUnsupported);

    /// <summary>Each single-purpose flag bit matches the ICD's Figure 5-3 bit position exactly (bit 0 through bit 4, low to high).</summary>
    [Fact]
    public void SinglePurposeFlags_MatchIcdBitPositions()
    {
        Assert.Equal((MsmtMessageFlags)0x1, MsmtMessageFlags.MessageSuccess);
        Assert.Equal((MsmtMessageFlags)0x2, MsmtMessageFlags.AcknowledgementRequestedOrGiven);
        Assert.Equal((MsmtMessageFlags)0x4, MsmtMessageFlags.ReachabilityCheck);
        Assert.Equal((MsmtMessageFlags)0x8, MsmtMessageFlags.InvalidPreambleOrModeUnsupported);
        Assert.Equal((MsmtMessageFlags)0x10, MsmtMessageFlags.SessionModeNegotiation);
    }
}
