namespace BlueHeighliner.Msmt;

/// <summary>
/// The progress of a tagged send queued via <see cref="IMsmtPeer.Send"/>, as reported by
/// <see cref="IMsmtPeer.PackageChanged"/>.
/// </summary>
public enum MsmtSendStatus
{
    /// <summary>The send has been queued and is waiting to be processed.</summary>
    Queued,

    /// <summary>The message is being written to the remote peer.</summary>
    Transmitting,

    /// <summary>
    /// The message has been fully sent and is awaiting the remote peer's acknowledgement. Skipped in favor
    /// of going straight to <see cref="Completed"/> for a message sent via <see cref="IMsmtPeer.Send"/>,
    /// which never requests one.
    /// </summary>
    PendingAcknowledgement,

    /// <summary>The send has finished, whether or not it succeeded.</summary>
    Completed,

    /// <summary>The send was cancelled via <see cref="IMsmtPackage.Cancel"/> before it completed.</summary>
    Cancelled,
}
