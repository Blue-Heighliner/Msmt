namespace BlueHeighliner.Msmt;

/// <summary>
/// A single tagged payload queued for delivery, letting its progress be polled or the send cancelled
/// without tracking the tag separately.
/// </summary>
public interface IMsmtPackage
{
    /// <summary>Gets the object reference this package was tagged with, as passed to <see cref="MsmtSendOptions.Tag"/>.</summary>
    object Tag { get; }

    /// <summary>Gets the remote peer this package was sent to, as passed to <see cref="IMsmtMessagePeer.Send"/> or <see cref="IMsmtSessionPeer.Connect"/>.</summary>
    MsmtNameTarget Target { get; }

    /// <summary>
    /// Gets this package's current status. Once it reaches <see cref="MsmtSendStatus.Completed"/> or <see
    /// cref="MsmtSendStatus.Cancelled"/>, it is latched permanently at that value from then on, even after
    /// the peer has otherwise forgotten this tag (see <see cref="IMsmtPeer.GetPackage"/>).
    /// </summary>
    MsmtSendStatus Status { get; }

    /// <summary>
    /// Requests cancellation of this package if it is still outstanding. Does nothing if it already
    /// completed or was already cancelled. Cancellation is best-effort: once in flight, requesting it forces
    /// the underlying connection closed right away, promptly interrupting a TLS handshake, write, or
    /// acknowledgement read already in progress rather than waiting for the remote peer.
    /// </summary>
    void Cancel();
}
