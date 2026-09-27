namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// A tagged payload tracked by an <see cref="IMsmtPackageTracker"/>, backing <see
/// cref="IMsmtPeer.Packages"/> and <see cref="IMsmtPeer.GetPackage"/>. Independent of the connection or
/// per-target sender that carried it, so it keeps working after that has been evicted.
/// </summary>
/// <param name="tracker">The tracker the tagged payload is registered with.</param>
/// <param name="tag">The tag identifying the payload.</param>
/// <param name="target">The remote peer the payload was sent to.</param>
/// <param name="initialStatus">The tag's status as of construction.</param>
internal sealed class MsmtPackage(IMsmtPackageTracker tracker, object tag, MsmtNameTarget target, MsmtSendStatus initialStatus) : IMsmtPackage
{
    private MsmtSendStatus status = initialStatus;
    private bool isFinal = initialStatus is MsmtSendStatus.Completed or MsmtSendStatus.Cancelled or MsmtSendStatus.Failed;

    /// <inheritdoc />
    public object Tag { get; } = tag;

    /// <inheritdoc />
    public MsmtNameTarget Target { get; } = target;

    /// <inheritdoc />
    public MsmtSendStatus Status
    {
        get
        {
            if (!isFinal)
            {
                status = tracker.GetStatus(Tag) ?? status;
                isFinal = status is MsmtSendStatus.Completed or MsmtSendStatus.Cancelled or MsmtSendStatus.Failed;
            }

            return status;
        }
    }

    /// <inheritdoc />
    public void Cancel() => tracker.Cancel(Tag);
}
