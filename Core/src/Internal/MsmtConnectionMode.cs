namespace BlueHeighliner.Msmt.Internal;

/// <summary>How a connection's TLS lifetime relates to the messages sent over it.</summary>
internal enum MsmtConnectionMode
{
    /// <summary>
    /// A fixed number of messages (see <see cref="MsmtConnectionSettings.RekeyLimit"/>), each after a TLS
    /// key update except the first, before the initiator closes the connection. <see
    /// cref="MsmtConnectionSettings.RekeyLimit"/> of <c>1</c> gives MSMT's plain Message mode - a fresh
    /// connection per message - and any higher value gives Message mode with rekeying.
    /// </summary>
    Requesting,

    /// <summary>A negotiated lifetime, bidirectional, kept alive by the initiator.</summary>
    Session,
}
