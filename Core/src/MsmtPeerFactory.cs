namespace BlueHeighliner.Msmt;

/// <summary>
/// Creates <see cref="IMsmtPeer"/> instances. Registered for dependency injection by <see
/// cref="MsmtServiceCollectionExtensions.AddMsmt"/>; construct <see cref="MsmtPeerFactory"/> directly,
/// with its parameterless constructor, when not using an IoC container.
/// </summary>
public interface IMsmtPeerFactory
{
    /// <summary>Creates a peer configured with the given options.</summary>
    /// <param name="options">The peer's shared credentials and connection-behavior defaults.</param>
    /// <returns>The new peer.</returns>
    IMsmtPeer Create(MsmtOptions options);
}

/// <inheritdoc cref="IMsmtPeerFactory" />
public sealed class MsmtPeerFactory : IMsmtPeerFactory
{
    /// <inheritdoc />
    public IMsmtPeer Create(MsmtOptions options) => new MsmtPeer(options);
}
