namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// Wraps a <see cref="ReadOnlyMemory{T}"/> that was never rented from a pool as an <see
/// cref="IMemoryOwner{T}"/>, so it can be handed to an API that requires one without copying it.
/// Disposal is a no-op, since there is no pooled buffer to return.
/// </summary>
/// <param name="memory">The memory to wrap. The caller must not mutate it for as long as this owner is in use.</param>
internal sealed class NonOwningMemoryOwner(ReadOnlyMemory<byte> memory) : IMemoryOwner<byte>
{
    /// <inheritdoc />
    public Memory<byte> Memory { get; } = MemoryMarshal.AsMemory(memory);

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
