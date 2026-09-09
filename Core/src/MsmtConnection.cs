namespace BlueHeighliner.Msmt;

/// <summary>
/// This peer's logical connection with a single remote peer, made up of up to two independent links: an
/// outgoing <see cref="Sender"/> this peer uses to send it messages, and an incoming <see cref="Receiver"/>
/// its receiver accepted to receive messages from it. Either may be <see langword="null"/> if that
/// direction is not currently open. Sending is done through the owning <see cref="IMsmtPeer"/>, not the
/// connection itself.
/// </summary>
public interface IMsmtConnection
{
    /// <summary>Gets the remote peer's address and port this connection is with.</summary>
    MsmtTarget Target { get; }

    /// <summary>Gets this peer's current outgoing link to the remote peer, or <see langword="null"/> if none is currently open.</summary>
    IMsmtLink? Sender { get; }

    /// <summary>Gets this peer's current incoming link from the remote peer, or <see langword="null"/> if none is currently open.</summary>
    IMsmtLink? Receiver { get; }

    /// <summary>
    /// Gets the remote peer's agreed-upon identity: <see cref="Sender"/>'s <see
    /// cref="IMsmtLink.Identity"/> if <see cref="Receiver"/> has none, <see cref="Receiver"/>'s if <see
    /// cref="Sender"/> has none, either one if both have a non-<see langword="null"/> identity and they
    /// match exactly, or <see langword="null"/> if neither link has completed a handshake yet, or their
    /// identities disagree.
    /// </summary>
    MsmtIdentity? Identity { get; }

    /// <summary>Immediately closes and discards both links, without waiting for either to finish.</summary>
    void Drop();

    /// <summary>Cleanly closes both links, waiting for each to finish before returning.</summary>
    /// <returns>A task that completes once both links have closed.</returns>
    Task Disconnect();
}
