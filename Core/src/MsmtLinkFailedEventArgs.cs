namespace BlueHeighliner.Msmt;

/// <summary>
/// Event data for an <see cref="IMsmtPeer"/>'s <see cref="IMsmtPeer.LinkFailed"/> event, identifying the
/// link whose attempt failed and the exception that caused it to fail.
/// </summary>
public sealed record MsmtLinkFailedEventArgs
{
    /// <summary>Gets the link whose attempt failed.</summary>
    public required IMsmtLink Link { get; init; }

    /// <summary>Gets the exception that caused the connection attempt to fail.</summary>
    public required Exception Exception { get; init; }
}
