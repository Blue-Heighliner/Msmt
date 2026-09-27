namespace BlueHeighliner.Msmt;

/// <summary>
/// The progress of a tagged send, as reported through a <see cref="MsmtPackageChange"/>.
/// </summary>
public enum MsmtSendStatus
{
    /// <summary>The send has been queued and is waiting to be processed.</summary>
    Queued,

    /// <summary>The message is being written to the remote peer.</summary>
    Transmitting,

    /// <summary>
    /// The message has been fully sent and is awaiting the remote peer's acknowledgement. Skipped in favor
    /// of going straight to <see cref="Completed"/> for a message that never requests one.
    /// </summary>
    PendingAcknowledgement,

    /// <summary>The send finished and was acknowledged by the remote peer.</summary>
    Completed,

    /// <summary>The send was cancelled via <see cref="IMsmtPackage.Cancel"/>, or abandoned because the connection closed, before it completed.</summary>
    Cancelled,

    /// <summary>The send failed, for example because the connection dropped or the remote peer did not answer in time. See <see cref="MsmtPackageChange.Exception"/>.</summary>
    Failed,
}
