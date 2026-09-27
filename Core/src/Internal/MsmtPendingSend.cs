namespace BlueHeighliner.Msmt.Internal;

/// <summary>A payload queued in an <see cref="MsmtOutbox"/>, waiting to be sent and acknowledged.</summary>
internal sealed record MsmtPendingSend
{
    /// <summary>Gets the message payload. Disposed by the outbox once the send finishes, however it ends.</summary>
    public required IMemoryOwner<byte> Payload { get; init; }

    /// <summary>Gets the flags the message is sent with.</summary>
    public required MsmtMessageFlags Flags { get; init; }

    /// <summary>Gets the caller's tag for tracking this send as a package, or <see langword="null"/> if untracked.</summary>
    public object? Tag { get; init; }

    /// <summary>Gets the DSCP to mark the message's packets with, or <see langword="null"/> to leave the socket unmarked.</summary>
    public int? Dscp { get; init; }

    /// <summary>Gets the caller's cancellation token for this send.</summary>
    public required CancellationToken Cancellation { get; init; }

    /// <summary>Gets the source completed with the acknowledgement of a <c>Request</c>, or <see langword="null"/> for a plain <c>Send</c>.</summary>
    public TaskCompletionSource<MsmtResponse>? ResponseSource { get; init; }

    /// <summary>Gets a value indicating whether this is an automatic keep-alive rather than application traffic.</summary>
    public bool IsKeepAlive { get; init; }

    /// <summary>Gets the source cancelled by <see cref="IMsmtPackage.Cancel"/>.</summary>
    public CancellationTokenSource CancelSource { get; } = new();
}
