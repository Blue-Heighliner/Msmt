namespace BlueHeighliner.Msmt;

/// <summary>
/// Event data identifying the <see cref="IMsmtConnection"/> an <see cref="IMsmtPeer"/>'s <see
/// cref="IMsmtPeer.Disconnected"/> event happened on.
/// </summary>
public sealed record MsmtDisconnectedEventArgs
{
    /// <summary>Gets the connection the event happened on.</summary>
    public required IMsmtConnection Connection { get; init; }
}
