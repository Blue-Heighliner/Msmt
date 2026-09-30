namespace BlueHeighliner.Msmt;

/// <summary>
/// Extension members for <see cref="IMemoryOwner{T}"/> of bytes.
/// </summary>
public static class MsmtMemoryOwnerExtensions
{
    extension(IMemoryOwner<byte> owner)
    {
        /// <summary>
        /// Wraps this owner so its <see cref="IMemoryOwner{T}.Memory"/> exposes only a sub-range of the
        /// buffer, while disposing the returned owner still disposes this one, returning the whole buffer to
        /// its pool. Needed because <see cref="MemoryPool{T}.Rent"/> may return a larger buffer than
        /// requested, and the whole of an owner's memory is what <see cref="IMsmtMessagePeer.Send"/>, <see
        /// cref="IMsmtMessagePeer.Request"/>, and <see cref="IMsmtResponder.Accept"/>/<see
        /// cref="IMsmtResponder.Reject"/> transmit.
        /// </summary>
        /// <param name="start">The offset of the first byte to expose.</param>
        /// <param name="length">The number of bytes to expose.</param>
        /// <returns>An owner of the same buffer exposing only the requested range. Ownership of this owner transfers to it; dispose only the returned one.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="start"/> or <paramref name="length"/> is negative, or the range extends past the end of this owner's memory.</exception>
        public IMemoryOwner<byte> Slice(int start, int length) => new SlicedMemoryOwner(owner, start, length);
    }
}
