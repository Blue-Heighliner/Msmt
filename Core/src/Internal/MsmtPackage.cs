namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// A tagged payload queued on a single <see cref="MsmtClient"/>, backing <see cref="IMsmtPeer.Packages"/>
/// and <see cref="IMsmtPeer.GetPackage"/>.
/// </summary>
/// <param name="client">The client the tagged payload was queued on.</param>
/// <param name="tag">The tag identifying the payload.</param>
/// <param name="initialStatus">The tag's status as of construction.</param>
internal sealed class MsmtPackage(MsmtClient client, object tag, MsmtSendStatus initialStatus) : IMsmtPackage
{
    private MsmtSendStatus status = initialStatus;
    private bool isFinal = initialStatus is MsmtSendStatus.Completed or MsmtSendStatus.Cancelled;

    /// <inheritdoc />
    public object Tag { get; } = tag;

    /// <inheritdoc />
    public MsmtNameTarget Target => client.Target;

    /// <inheritdoc />
    public MsmtSendStatus Status
    {
        get
        {
            if (!isFinal)
            {
                status = client.GetStatus(Tag) ?? status;
                isFinal = status is MsmtSendStatus.Completed or MsmtSendStatus.Cancelled;
            }

            return status;
        }
    }

    /// <inheritdoc />
    public void Cancel() => client.Cancel(Tag);
}
