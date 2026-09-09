namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// The MSMT message header's flag bits, carried on every message and acknowledgement.
/// </summary>
/// <remarks>
/// Bit values match the ICD's Figure 5-3 exactly (bit 0 through bit 4, low to high); all remaining bits,
/// including the rest of the 16-bit flags field, are reserved for future use and must be zero. Some
/// members, such as <see cref="ReachabilityCheck"/> and the session negotiation outcomes, are themselves
/// combinations of the single-purpose bits below rather than independent bits of their own.
/// </remarks>
[Flags]
internal enum MsmtMessageFlags : ushort
{
    /// <summary>No flags set.</summary>
    None = 0x0,

    /// <summary>Set by the receiver to indicate the message was valid and processed successfully.</summary>
    MessageSuccess = 0x1,

    /// <summary>Set by the sender to request an acknowledgement, and mirrored by the receiver to indicate one was given.</summary>
    AcknowledgementRequestedOrGiven = 0x2,

    /// <summary>Set on a reachability check message and its acknowledgement.</summary>
    ReachabilityCheck = 0x4,

    /// <summary>Set by the receiver to indicate the received message header was malformed or used an unsupported API version.</summary>
    InvalidPreambleOrModeUnsupported = 0x8,

    /// <summary>Set by either side to signify this message is negotiating (client) or responding to (server) a <see cref="MsmtOperationMode.Session"/> connection lifetime.</summary>
    SessionModeNegotiation = 0x10,

    /// <summary>Set by a server accepting a Session Mode negotiation request, together with the agreed lifetime encoded in the acknowledgement payload.</summary>
    SessionModeAccepted = SessionModeNegotiation | MessageSuccess,

    /// <summary>Set by a server rejecting a repeated Session Mode negotiation request on an already-negotiated connection.</summary>
    SessionModeRejected = SessionModeNegotiation,

    /// <summary>Set by a server that does not support Session Mode at all, in response to a negotiation request.</summary>
    SessionModeUnsupported = SessionModeNegotiation | InvalidPreambleOrModeUnsupported,
}
