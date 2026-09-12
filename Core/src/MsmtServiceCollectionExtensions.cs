namespace BlueHeighliner.Msmt;

/// <summary>
/// Extension members for <see cref="IServiceCollection"/>.
/// </summary>
public static class MsmtServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>Registers <see cref="IMsmtPeerFactory"/> so <see cref="IMsmtPeer"/> instances can be created via dependency injection.</summary>
        /// <returns>The same service collection, for chaining.</returns>
        public IServiceCollection AddMsmt()
        {
            services.TryAddSingleton<IMsmtPeerFactory, MsmtPeerFactory>();
            return services;
        }
    }
}
