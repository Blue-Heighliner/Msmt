namespace BlueHeighliner.Msmt;

/// <summary>
/// Event data for a message an <see cref="IMsmtPeer"/> received, raised via its <see
/// cref="IMsmtPeer.Received"/> event.
/// </summary>
public sealed record MsmtReceivedEventArgs
{
    /// <summary>Gets the receiving link the message was received on.</summary>
    public required IMsmtLink Link { get; init; }

    /// <summary>
    /// Gets the message payload, exactly as sent by the client. Rented from a pool and disposed once every
    /// subscriber of this event has run; a subscriber that needs to keep it longer must clone it.
    /// </summary>
    public required IMemoryOwner<byte> Payload { get; init; }

    /// <summary>Gets the responder to acknowledge this message with, if <see cref="IsResponseRequested"/> is <see langword="true"/>.</summary>
    public required IMsmtResponder Responder { get; init; }

    /// <summary>Gets a value indicating whether the sender requested an acknowledgement for this message.</summary>
    public required bool IsResponseRequested { get; init; }
}
