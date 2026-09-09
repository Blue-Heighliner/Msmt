namespace BlueHeighliner.Msmt;

/// <summary>
/// Event data identifying the <see cref="IMsmtLink"/> an <see cref="IMsmtPeer"/>'s <see
/// cref="IMsmtPeer.Linking"/> event happened on.
/// </summary>
public sealed record MsmtLinkingEventArgs
{
    /// <summary>Gets the link the event happened on.</summary>
    public required IMsmtLink Link { get; init; }
}
