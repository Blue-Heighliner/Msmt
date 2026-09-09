namespace BlueHeighliner.Msmt;

/// <summary>
/// Event data for a tagged send's progress, raised via <see cref="IMsmtPeer.PackageChanged"/>.
/// </summary>
public sealed record MsmtPackageChangedEventArgs
{
    /// <summary>Gets the sending link the send is running on.</summary>
    public required IMsmtLink Link { get; init; }

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
}
