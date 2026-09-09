namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// A reusable, always-empty <see cref="IMemoryOwner{T}"/> for payloads that carry no data, such as an
/// automatic keep-alive reachability check.
/// </summary>
internal sealed class EmptyMemoryOwner : IMemoryOwner<byte>
{
    /// <summary>Gets the shared singleton instance.</summary>
    public static EmptyMemoryOwner Instance { get; } = new();

    /// <inheritdoc />
    public Memory<byte> Memory => Memory<byte>.Empty;

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
