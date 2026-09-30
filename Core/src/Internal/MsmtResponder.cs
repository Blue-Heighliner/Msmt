namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// Answers one received message through a delegate that queues the acknowledgement, enforcing that it is
/// answered at most once.
/// </summary>
/// <param name="send">Queues an acknowledgement, given whether it is positive and its payload, which it takes ownership of.</param>
internal sealed class MsmtResponder(Action<bool, IMemoryOwner<byte>?> send) : IMsmtResponder
{
    private int isAnswered;

    /// <inheritdoc />
    public void Accept(IMemoryOwner<byte>? payload = null) => Answer(true, payload);

    /// <inheritdoc />
    public void Reject(IMemoryOwner<byte>? payload = null) => Answer(false, payload);

    private void Answer(bool success, IMemoryOwner<byte>? payload)
    {
        if (payload is not null)
        {
            MsmtProtocol.ValidatePayloadLength(payload.Memory.Length, nameof(payload));
        }

        if (Interlocked.Exchange(ref isAnswered, 1) != 0)
        {
            throw new InvalidOperationException("The message was already answered.");
        }

        send(success, payload);
    }
}
