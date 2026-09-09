namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// Wraps a rented <see cref="IMemoryOwner{T}"/> and exposes only a requested prefix of its buffer - which
/// a pool is free to return larger than requested - while still returning the entire underlying buffer to
/// the pool on disposal.
/// </summary>
/// <param name="inner">The rented owner to wrap and eventually dispose.</param>
/// <param name="length">The number of bytes, from the start of <paramref name="inner"/>'s buffer, to expose through <see cref="Memory"/>.</param>
internal sealed class SlicedMemoryOwner(IMemoryOwner<byte> inner, int length) : IMemoryOwner<byte>
{
    /// <inheritdoc />
    public Memory<byte> Memory { get; } = inner.Memory[..length];

    /// <inheritdoc />
    public void Dispose() => inner.Dispose();
}
