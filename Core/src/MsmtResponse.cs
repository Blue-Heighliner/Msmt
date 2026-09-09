namespace BlueHeighliner.Msmt;

/// <summary>
/// The remote peer's acknowledgement of a message sent via <see cref="IMsmtPeer.Request"/>.
/// </summary>
public sealed record MsmtResponse
{
    /// <summary>Gets a value indicating whether the remote peer reported the message as successfully processed.</summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Gets the response payload returned by the remote peer. Rented from a pool; ownership transfers to
    /// the caller of <see cref="IMsmtPeer.Request"/>, who must dispose it once done.
    /// </summary>
    public required IMemoryOwner<byte> Payload { get; init; }
}
