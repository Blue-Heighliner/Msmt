namespace BlueHeighliner.Msmt;

/// <summary>
/// One direction of communication with a remote peer, backed by its own TLS session: either the outgoing
/// <see cref="IMsmtConnection.Sender"/> this peer uses to send messages, or the incoming <see
/// cref="IMsmtConnection.Receiver"/> its receiver accepted to receive them.
/// </summary>
public interface IMsmtLink
{
    /// <summary>Gets a value indicating whether this is the sending (outgoing) or receiving (incoming) direction of its <see cref="Connection"/>.</summary>
    MsmtLinkType Kind { get; }

    /// <summary>
    /// Gets a snapshot of the remote peer's TLS certificate, as presented and verified during the most
    /// recently completed handshake, or <see langword="null"/> if this link has not completed its initial
    /// handshake yet. Keeps reflecting the last-negotiated identity even after the link closes, until a new
    /// handshake replaces it.
    /// </summary>
    MsmtIdentity? Identity { get; }

    /// <summary>Gets the connection this link belongs to.</summary>
    IMsmtConnection Connection { get; }

    /// <summary>Immediately closes and discards this link, without waiting for it to finish.</summary>
    void Drop();

    /// <summary>Cleanly closes this link, waiting for it to finish before returning.</summary>
    /// <returns>A task that completes once this link has closed.</returns>
    Task Disconnect();
}
