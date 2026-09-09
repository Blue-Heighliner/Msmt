namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// The fixed 10-byte header prepended to every MSMT message and acknowledgement, encoded in network
/// byte order for every multi-byte field.
/// </summary>
internal readonly record struct MsmtHeader
{
    /// <summary>Gets the only API version this implementation understands (MSMT v1.2, with the Message ID field).</summary>
    public static byte SupportedVersion { get; } = 3;

    /// <summary>Gets the header's fixed on-wire size, in bytes.</summary>
    public static int Size { get; } = 10;

    /// <summary>Gets the maximum payload length this implementation accepts, matching the reference MSMT implementation's message-size cap.</summary>
    public static uint MaxLength { get; } = (uint)MsmtLimits.MaxPayloadLength;

    /// <summary>Gets the flag bits this implementation defines; a header setting any other bit is invalid.</summary>
    public static MsmtMessageFlags DefinedFlags { get; } =
        MsmtMessageFlags.MessageSuccess | MsmtMessageFlags.AcknowledgementRequestedOrGiven | MsmtMessageFlags.ReachabilityCheck | MsmtMessageFlags.InvalidPreambleOrModeUnsupported | MsmtMessageFlags.SessionModeNegotiation;

    /// <summary>
    /// Reads a header from <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The buffer to read from; must be at least <see cref="Size"/> bytes.</param>
    /// <returns>The decoded header.</returns>
    public static MsmtHeader Read(ReadOnlySpan<byte> source) => new()
    {
        Version = source[0],
        Flags = (MsmtMessageFlags)BinaryPrimitives.ReadUInt16BigEndian(source[2..]),
        MessageId = BinaryPrimitives.ReadUInt16BigEndian(source[4..]),
        Length = BinaryPrimitives.ReadUInt32BigEndian(source[6..]),
    };

    /// <summary>Gets the API version the sender used.</summary>
    public required byte Version { get; init; }

    /// <summary>Gets the flags carried alongside this message.</summary>
    public required MsmtMessageFlags Flags { get; init; }

    /// <summary>Gets the randomly generated identifier correlating a message with its acknowledgement.</summary>
    public required ushort MessageId { get; init; }

    /// <summary>Gets the length, in bytes, of the payload following this header.</summary>
    public required uint Length { get; init; }

    /// <summary>
    /// Writes this header to <paramref name="destination"/> in network byte order. The ICD's "rsv" byte
    /// (offset 1) is always written as zero, as required for API version 3.
    /// </summary>
    /// <param name="destination">The buffer to write into; must be at least <see cref="Size"/> bytes.</param>
    public void Write(Span<byte> destination)
    {
        destination[0] = Version;
        destination[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)Flags);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], MessageId);
        BinaryPrimitives.WriteUInt32BigEndian(destination[6..], Length);
    }

    /// <summary>
    /// Determines whether this header uses a supported API version, sets no undefined flag bits, and
    /// declares a payload length within <see cref="MaxLength"/>.
    /// </summary>
    /// <returns><see langword="true"/> if the header is well-formed.</returns>
    public bool IsWellFormed() => Version == SupportedVersion && (Flags & ~DefinedFlags) == 0 && Length <= MaxLength;

    /// <summary>
    /// Determines whether this header is a valid acknowledgement of <paramref name="request"/> - the
    /// same version and message ID.
    /// </summary>
    /// <param name="request">The header of the message this is expected to acknowledge.</param>
    /// <returns><see langword="true"/> if this header corresponds to <paramref name="request"/>.</returns>
    public bool Acknowledges(MsmtHeader request) =>
        Version == request.Version && MessageId == request.MessageId;
}
