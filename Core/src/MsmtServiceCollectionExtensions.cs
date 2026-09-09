namespace BlueHeighliner.Msmt;

/// <summary>
/// Extension methods for registering MSMT services into an <see cref="IServiceCollection"/>.
/// </summary>
public static class MsmtServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IMsmtPeerFactory"/> so <see cref="IMsmtPeer"/> instances can be created via dependency injection.</summary>
    /// <param name="services">The service collection to register into.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddMsmt(this IServiceCollection services)
    {
        services.TryAddSingleton<IMsmtPeerFactory, MsmtPeerFactory>();
        return services;
    }
}
