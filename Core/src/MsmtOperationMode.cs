namespace BlueHeighliner.Msmt;

/// <summary>
/// Selects how an MSMT connection's TLS lifetime relates to the messages sent over it.
/// </summary>
public enum MsmtOperationMode
{
    /// <summary>
    /// Establishes a brand-new TLS connection for every message and tears it down immediately after
    /// the acknowledgement is received. The default, and most secure, mode.
    /// </summary>
    Message,

    /// <summary>
    /// Reuses a single TLS connection across messages, triggering a TLS 1.3 key update between each
    /// one instead of a full handshake, up to <see cref="MsmtOptions.RekeyLimit"/> messages
    /// before a full connection reset is performed.
    /// </summary>
    MessageWithRekeying,

    /// <summary>
    /// Negotiates a maximum TLS connection lifetime immediately after the handshake completes, then
    /// reuses that single connection for an arbitrary number of messages until the lifetime expires.
    /// </summary>
    Session,
}
