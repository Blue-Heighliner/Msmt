namespace BlueHeighliner.Msmt;

/// <summary>
/// The decision an <see cref="MsmtMessageReceiver"/> or <see cref="MsmtSessionReceiver"/> returns for a
/// message that requested an acknowledgement: positive or negative, with an optional payload of its own.
/// </summary>
public readonly record struct MsmtReceiveResult
{
    /// <summary>Gets a positive acknowledgement with an empty payload.</summary>
    public static MsmtReceiveResult Accept() => new() { Success = true };

    /// <summary>Gets a positive acknowledgement carrying <paramref name="payload"/>.</summary>
    /// <param name="payload">
    /// The acknowledgement payload, rented from a pool. Ownership transfers to the caller that receives
    /// this result, which disposes it once the acknowledgement has been sent.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
    public static MsmtReceiveResult Accept(IMemoryOwner<byte> payload)
    {
        MsmtProtocol.ValidatePayloadLength(payload.Memory.Length, nameof(payload));
        return new MsmtReceiveResult { Success = true, Payload = payload };
    }

    /// <summary>Gets a positive acknowledgement carrying <paramref name="payload"/>, wrapping it in a non-pooled <see cref="IMemoryOwner{T}"/> so callers with an ordinary <see cref="ReadOnlyMemory{T}"/> don't need to manage one themselves.</summary>
    /// <param name="payload">The acknowledgement payload. Not copied - the caller must not mutate it until the acknowledgement has been sent.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
    public static MsmtReceiveResult Accept(ReadOnlyMemory<byte> payload) => Accept(new NonOwningMemoryOwner(payload));

    /// <summary>Gets a negative acknowledgement with an empty payload.</summary>
    public static MsmtReceiveResult Reject() => new() { Success = false };

    /// <summary>Gets a negative acknowledgement carrying <paramref name="payload"/>.</summary>
    /// <param name="payload">
    /// The acknowledgement payload, rented from a pool. Ownership transfers to the caller that receives
    /// this result, which disposes it once the acknowledgement has been sent.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
    public static MsmtReceiveResult Reject(IMemoryOwner<byte> payload)
    {
        MsmtProtocol.ValidatePayloadLength(payload.Memory.Length, nameof(payload));
        return new MsmtReceiveResult { Success = false, Payload = payload };
    }

    /// <summary>Gets a negative acknowledgement carrying <paramref name="payload"/>, wrapping it in a non-pooled <see cref="IMemoryOwner{T}"/> so callers with an ordinary <see cref="ReadOnlyMemory{T}"/> don't need to manage one themselves.</summary>
    /// <param name="payload">The acknowledgement payload. Not copied - the caller must not mutate it until the acknowledgement has been sent.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
    public static MsmtReceiveResult Reject(ReadOnlyMemory<byte> payload) => Reject(new NonOwningMemoryOwner(payload));

    /// <summary>Gets a value indicating whether the message is positively acknowledged.</summary>
    public required bool Success { get; init; }

    /// <summary>Gets the acknowledgement payload, or <see langword="null"/> for an empty one.</summary>
    public IMemoryOwner<byte>? Payload { get; init; }
}
