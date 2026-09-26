namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// Wraps an <see cref="IMemoryOwner{T}"/> and exposes only a sub-range of its buffer - e.g. the requested
/// prefix of a rented buffer, which a pool is free to return larger than requested - while still returning
/// the entire underlying buffer to its pool on disposal.
/// </summary>
/// <param name="inner">The owner to wrap and eventually dispose.</param>
/// <param name="start">The offset, into <paramref name="inner"/>'s buffer, of the first byte to expose through <see cref="Memory"/>.</param>
/// <param name="length">The number of bytes, from <paramref name="start"/>, to expose through <see cref="Memory"/>.</param>
/// <exception cref="ArgumentOutOfRangeException"><paramref name="start"/> or <paramref name="length"/> is negative, or the range extends past the end of <paramref name="inner"/>'s buffer.</exception>
internal sealed class SlicedMemoryOwner(IMemoryOwner<byte> inner, int start, int length) : IMemoryOwner<byte>
{
    /// <inheritdoc />
    public Memory<byte> Memory { get; } = inner.Memory.Slice(start, length);

    /// <inheritdoc />
    public void Dispose() => inner.Dispose();
}
