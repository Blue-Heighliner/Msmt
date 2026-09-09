namespace BlueHeighliner.Msmt;

/// <summary>
/// Event data for an <see cref="IMsmtPeer"/>'s <see cref="IMsmtPeer.Unlinked"/> event, identifying the
/// link that closed and, if it closed because of an error, the exception that caused it.
/// </summary>
public sealed record MsmtUnlinkedEventArgs
{
    /// <summary>Gets the link that closed.</summary>
    public required IMsmtLink Link { get; init; }

    /// <summary>Gets the exception that caused the link to close, or <see langword="null"/> if it closed cleanly.</summary>
    public Exception? Exception { get; init; }
}
