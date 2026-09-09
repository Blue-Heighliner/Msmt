namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// Shared wire-level helpers used by both <see cref="MsmtClient"/> and <see cref="MsmtServer"/>.
/// </summary>
internal static class MsmtProtocol
{
    /// <summary>
    /// Gets the longest duration a single <see cref="CancellationTokenSource"/>/<see
    /// cref="Task.Delay(TimeSpan)"/> timer supports (they throw <see cref="ArgumentOutOfRangeException"/>
    /// beyond it) - just under <see cref="uint.MaxValue"/> milliseconds, roughly 49.7 days.
    /// </summary>
    public static TimeSpan MaxTimerDuration { get; } = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// Generates a random 16-bit message identifier to correlate a message with its acknowledgement.
    /// </summary>
    /// <returns>A random message identifier.</returns>
    public static ushort GenerateMessageId() => (ushort)Random.Shared.Next(ushort.MinValue, ushort.MaxValue + 1);

    /// <summary>
    /// Clamps <paramref name="duration"/> to <see cref="MaxTimerDuration"/>, so it can always be passed to a
    /// single <see cref="CancellationTokenSource"/>/<see cref="Task.Delay(TimeSpan)"/> timer without it
    /// throwing, regardless of how long <paramref name="duration"/> actually is.
    /// </summary>
    /// <param name="duration">The duration to clamp.</param>
    /// <returns>The lesser of <paramref name="duration"/> and <see cref="MaxTimerDuration"/>.</returns>
    public static TimeSpan ClampToMaxTimerDuration(TimeSpan duration) =>
        duration > MaxTimerDuration ? MaxTimerDuration : duration;

    /// <summary>
    /// Throws if <paramref name="length"/> exceeds <see cref="MsmtLimits.MaxPayloadLength"/> - the largest
    /// payload an MSMT header can declare and still be considered well-formed by the receiving peer.
    /// Validated before a payload is queued or written, so an oversized one fails fast with a clear error
    /// rather than being transmitted and then rejected as malformed mid-connection.
    /// </summary>
    /// <param name="length">The payload length, in bytes, to validate.</param>
    /// <param name="parameterName">The caller's payload parameter name, reported on the thrown exception.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> exceeds <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
    public static void ValidatePayloadLength(int length, string parameterName)
    {
        if (length > MsmtLimits.MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, length, $"An MSMT payload may be at most {MsmtLimits.MaxPayloadLength} bytes.");
        }
    }

    /// <summary>
    /// Reads exactly <paramref name="buffer"/>'s length from <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="buffer">The buffer to fill.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <exception cref="IOException">The remote peer closed the connection before <paramref name="buffer"/> was filled.</exception>
    public static async Task ReadExact(Stream stream, Memory<byte> buffer, CancellationToken cancellation)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int chunk = await stream.ReadAsync(buffer[read..], cancellation);
            if (chunk == 0)
            {
                throw new IOException("The remote peer closed the connection before the expected data was fully received.");
            }

            read += chunk;
        }
    }

    /// <summary>
    /// Reads exactly <paramref name="length"/> bytes from <paramref name="stream"/> into a buffer rented
    /// from <see cref="MemoryPool{T}.Shared"/>, so callers can hand payloads to applications without an
    /// allocation per message.
    /// </summary>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="length">The number of bytes to read.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>An owner of the rented buffer, sliced to exactly <paramref name="length"/> bytes.</returns>
    /// <exception cref="IOException">The remote peer closed the connection before <paramref name="length"/> bytes were received.</exception>
    public static async Task<IMemoryOwner<byte>> ReadPooled(Stream stream, int length, CancellationToken cancellation)
    {
        IMemoryOwner<byte> owner = new SlicedMemoryOwner(MemoryPool<byte>.Shared.Rent(length), length);
        await ReadExact(stream, owner.Memory, cancellation);
        return owner;
    }

    /// <summary>
    /// Copies <paramref name="source"/> into a new buffer rented from <see cref="MemoryPool{T}.Shared"/>.
    /// </summary>
    /// <param name="source">The data to copy.</param>
    /// <returns>An owner of the rented buffer, containing a copy of <paramref name="source"/>.</returns>
    public static IMemoryOwner<byte> ClonePooled(ReadOnlyMemory<byte> source)
    {
        IMemoryOwner<byte> owner = new SlicedMemoryOwner(MemoryPool<byte>.Shared.Rent(source.Length), source.Length);
        source.CopyTo(owner.Memory);
        return owner;
    }

    /// <summary>
    /// Resolves <paramref name="host"/> to a listenable local address: parsed directly if it's already an
    /// IP address literal, otherwise resolved via DNS.
    /// </summary>
    /// <param name="host">An IP address literal or DNS hostname.</param>
    /// <returns>The resolved address.</returns>
    public static IPAddress ResolveAddress(string host) =>
        IPAddress.TryParse(host, out IPAddress? address) ? address : Dns.GetHostAddresses(host)[0];
}
