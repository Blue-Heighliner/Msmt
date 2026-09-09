namespace BlueHeighliner.Msmt.Tests.Unit;

/// <summary>Unit tests for <see cref="MsmtHeader"/>.</summary>
public sealed class MsmtHeaderTests
{
    /// <summary>Writing then reading a header round-trips every field exactly, in network byte order.</summary>
    [Fact]
    public void WriteThenRead_AnyHeader_RoundTripsExactly()
    {
        MsmtHeader header = new()
        {
            Version = MsmtHeader.SupportedVersion,
            Flags = MsmtMessageFlags.MessageSuccess | MsmtMessageFlags.AcknowledgementRequestedOrGiven,
            MessageId = 0xBEEF,
            Length = 0x00ABCDEF,
        };

        byte[] buffer = new byte[MsmtHeader.Size];
        header.Write(buffer);
        MsmtHeader roundTripped = MsmtHeader.Read(buffer);

        Assert.Equal(header, roundTripped);
    }

    /// <summary>The reserved byte at offset 1 is always written as zero, regardless of what garbage occupied it before.</summary>
    [Fact]
    public void Write_ReservedByte_AlwaysZero()
    {
        MsmtHeader header = new() { Version = MsmtHeader.SupportedVersion, Flags = MsmtMessageFlags.None, MessageId = 0, Length = 0 };
        byte[] buffer = new byte[MsmtHeader.Size];
        buffer[1] = 0xFF;

        header.Write(buffer);

        Assert.Equal(0, buffer[1]);
    }

    /// <summary>Every multi-byte field is written in network byte order (big-endian).</summary>
    [Fact]
    public void Write_MultiByteFields_UseNetworkByteOrder()
    {
        MsmtHeader header = new() { Version = 3, Flags = (MsmtMessageFlags)0x0102, MessageId = 0x0304, Length = 0x05060708 };
        byte[] buffer = new byte[MsmtHeader.Size];

        header.Write(buffer);

        Assert.Equal(3, buffer[0]);
        Assert.Equal([0x01, 0x02], buffer[2..4]);
        Assert.Equal([0x03, 0x04], buffer[4..6]);
        Assert.Equal([0x05, 0x06, 0x07, 0x08], buffer[6..10]);
    }

    /// <summary>A header using the supported version, only defined flags, and a length within the maximum is well-formed.</summary>
    [Fact]
    public void IsWellFormed_SupportedVersionDefinedFlagsWithinMaxLength_ReturnsTrue()
    {
        MsmtHeader header = new() { Version = MsmtHeader.SupportedVersion, Flags = MsmtMessageFlags.MessageSuccess, MessageId = 1, Length = MsmtHeader.MaxLength };

        Assert.True(header.IsWellFormed());
    }

    /// <summary>A header using any version other than <see cref="MsmtHeader.SupportedVersion"/> is not well-formed.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void IsWellFormed_UnsupportedVersion_ReturnsFalse(byte version)
    {
        MsmtHeader header = new() { Version = version, Flags = MsmtMessageFlags.None, MessageId = 1, Length = 0 };

        Assert.False(header.IsWellFormed());
    }

    /// <summary>A header setting any bit outside <see cref="MsmtHeader.DefinedFlags"/> is not well-formed.</summary>
    [Fact]
    public void IsWellFormed_UndefinedFlagBitSet_ReturnsFalse()
    {
        MsmtHeader header = new() { Version = MsmtHeader.SupportedVersion, Flags = (MsmtMessageFlags)0x8000, MessageId = 1, Length = 0 };

        Assert.False(header.IsWellFormed());
    }

    /// <summary>A header declaring a length beyond <see cref="MsmtHeader.MaxLength"/> is not well-formed.</summary>
    [Fact]
    public void IsWellFormed_LengthBeyondMaxLength_ReturnsFalse()
    {
        MsmtHeader header = new() { Version = MsmtHeader.SupportedVersion, Flags = MsmtMessageFlags.None, MessageId = 1, Length = MsmtHeader.MaxLength + 1 };

        Assert.False(header.IsWellFormed());
    }

    /// <summary>A response header with the same version and message ID acknowledges its request.</summary>
    [Fact]
    public void Acknowledges_SameVersionAndMessageId_ReturnsTrue()
    {
        MsmtHeader request = new() { Version = MsmtHeader.SupportedVersion, Flags = MsmtMessageFlags.None, MessageId = 42, Length = 0 };
        MsmtHeader response = new() { Version = MsmtHeader.SupportedVersion, Flags = MsmtMessageFlags.MessageSuccess, MessageId = 42, Length = 0 };

        Assert.True(response.Acknowledges(request));
    }

    /// <summary>A response header with a different message ID does not acknowledge the request.</summary>
    [Fact]
    public void Acknowledges_DifferentMessageId_ReturnsFalse()
    {
        MsmtHeader request = new() { Version = MsmtHeader.SupportedVersion, Flags = MsmtMessageFlags.None, MessageId = 42, Length = 0 };
        MsmtHeader response = new() { Version = MsmtHeader.SupportedVersion, Flags = MsmtMessageFlags.None, MessageId = 43, Length = 0 };

        Assert.False(response.Acknowledges(request));
    }

    /// <summary>A response header with a different version does not acknowledge the request.</summary>
    [Fact]
    public void Acknowledges_DifferentVersion_ReturnsFalse()
    {
        MsmtHeader request = new() { Version = MsmtHeader.SupportedVersion, Flags = MsmtMessageFlags.None, MessageId = 42, Length = 0 };
        MsmtHeader response = new() { Version = (byte)(MsmtHeader.SupportedVersion + 1), Flags = MsmtMessageFlags.None, MessageId = 42, Length = 0 };

        Assert.False(response.Acknowledges(request));
    }
}
