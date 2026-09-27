namespace BlueHeighliner.Msmt;

/// <summary>
/// Event data for a tagged send's progress, raised as its <see cref="IMsmtPackage"/> changes status.
/// </summary>
public sealed record MsmtPackageChange
{
    /// <summary>Gets the connection the send went over, for an <see cref="IMsmtSessionPeer"/>'s <see cref="IMsmtPeer.PackageChanged"/>; <see langword="null"/> for an <see cref="IMsmtMessagePeer"/>'s, which hides its connections.</summary>
    public IMsmtConnection? Connection { get; init; }

    /// <summary>Gets the package whose status changed.</summary>
    public required IMsmtPackage Package { get; init; }

    /// <summary>
    /// Gets the status <see cref="Package"/> changed to, as of when this event was raised. Unlike <see
    /// cref="IMsmtPackage.Status"/>, which always reflects the package's current status, this value is
    /// fixed at the moment of this event - useful since the package may have already progressed further
    /// by the time a subscriber gets to it, e.g. because an earlier subscriber ran slowly, or the
    /// underlying send kept advancing concurrently on another thread.
    /// </summary>
    public required MsmtSendStatus Status { get; init; }

    /// <summary>Gets why the send failed when <see cref="Status"/> is <see cref="MsmtSendStatus.Failed"/>, or <see langword="null"/> otherwise.</summary>
    public Exception? Exception { get; init; }
}
