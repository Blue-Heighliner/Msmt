namespace BlueHeighliner.Msmt;

/// <summary>
/// Lets the application answer one message that requested an acknowledgement, positively or negatively and
/// with an optional payload of its own. Handed to an <see cref="MsmtMessageReceiver"/> or <see
/// cref="MsmtSessionReceiver"/> only for such a message. Answering is up to the application, from any
/// thread and at any time: it may be done inside the receiver or long after it returned, the receiver
/// returning means nothing to it, and the sender's <see cref="MsmtOptions.ResponseTimeout"/> is what
/// bounds a message that is never answered. A message can be answered only once.
/// </summary>
public interface IMsmtResponder
{
    /// <summary>Queues a positive acknowledgement to be written, without waiting for it to be.</summary>
    /// <param name="payload">
    /// The acknowledgement payload, rented from a pool, or <see langword="null"/> for an empty one. If this
    /// method returns without throwing, ownership of <paramref name="payload"/> transfers to it, which
    /// disposes it once the acknowledgement has been written; if it throws, the caller retains ownership.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
    /// <exception cref="InvalidOperationException">This message was already answered.</exception>
    void Accept(IMemoryOwner<byte>? payload = null);

    /// <summary>Queues a negative acknowledgement to be written, without waiting for it to be.</summary>
    /// <param name="payload">
    /// The acknowledgement payload, rented from a pool, or <see langword="null"/> for an empty one. If this
    /// method returns without throwing, ownership of <paramref name="payload"/> transfers to it, which
    /// disposes it once the acknowledgement has been written; if it throws, the caller retains ownership.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
    /// <exception cref="InvalidOperationException">This message was already answered.</exception>
    void Reject(IMemoryOwner<byte>? payload = null);
}

/// <summary>
/// Extension members for <see cref="IMsmtResponder"/>.
/// </summary>
public static class MsmtResponderExtensions
{
    extension(IMsmtResponder responder)
    {
        /// <summary>
        /// Sends a positive acknowledgement carrying <paramref name="payload"/>, wrapping it in a non-pooled
        /// <see cref="IMemoryOwner{T}"/> so callers with an ordinary <see cref="ReadOnlyMemory{T}"/> don't
        /// need to manage one themselves to call <see cref="IMsmtResponder.Accept"/>.
        /// </summary>
        /// <param name="payload">The acknowledgement payload. Not copied - the caller must not mutate it until the acknowledgement has been written.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
        /// <exception cref="InvalidOperationException">The message was already answered.</exception>
        public void Accept(ReadOnlyMemory<byte> payload) => responder.Accept(new NonOwningMemoryOwner(payload));

        /// <summary>
        /// Sends a negative acknowledgement carrying <paramref name="payload"/>, wrapping it in a non-pooled
        /// <see cref="IMemoryOwner{T}"/> so callers with an ordinary <see cref="ReadOnlyMemory{T}"/> don't
        /// need to manage one themselves to call <see cref="IMsmtResponder.Reject"/>.
        /// </summary>
        /// <param name="payload">The acknowledgement payload. Not copied - the caller must not mutate it until the acknowledgement has been written.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
        /// <exception cref="InvalidOperationException">The message was already answered.</exception>
        public void Reject(ReadOnlyMemory<byte> payload) => responder.Reject(new NonOwningMemoryOwner(payload));
    }
}
