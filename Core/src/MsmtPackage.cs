namespace BlueHeighliner.Msmt;

/// <summary>
/// A single tagged payload queued for delivery via <see cref="IMsmtPeer.Send"/>, letting its progress be
/// polled or the send cancelled without tracking the tag separately.
/// </summary>
public interface IMsmtPackage
{
    /// <summary>Gets the object reference this package was tagged with, as passed to <see cref="MsmtSendOptions.Tag"/>.</summary>
    object Tag { get; }

    /// <summary>Gets the remote peer this package was sent to, as passed to <see cref="IMsmtPeer.Send"/>.</summary>
    MsmtNameTarget Target { get; }

    /// <summary>
    /// Gets this package's current status. Once it reaches <see cref="MsmtSendStatus.Completed"/> or <see
    /// cref="MsmtSendStatus.Cancelled"/>, it is latched permanently at that value from then on, even after
    /// the owning connection has otherwise forgotten this tag (see <see cref="IMsmtPeer.GetPackage"/>).
    /// </summary>
    MsmtSendStatus Status { get; }

    /// <summary>
    /// Requests cancellation of this package if it is still outstanding. Does nothing if it already
    /// completed or was already cancelled. Cancellation is best-effort: once queued, requesting it forces
    /// the underlying connection closed right away, promptly unblocking a write or acknowledgement read
    /// already in flight rather than waiting for the remote peer - except that a TLS handshake already in
    /// progress cannot be interrupted.
    /// </summary>
    void Cancel();
}
