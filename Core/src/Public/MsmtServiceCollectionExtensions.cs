namespace BlueHeighliner.Msmt;

/// <summary>
/// Extension members for <see cref="IServiceCollection"/>.
/// </summary>
public static class MsmtServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers <see cref="IMsmtMessagePeer.IFactory"/>, <see cref="IMsmtSessionPeer.IFactory"/> and
        /// <see cref="IMsmtReachabilityChecker"/> so peers, session peers and a reachability checker can be
        /// created via dependency injection.
        /// </summary>
        /// <returns>The same service collection, for chaining.</returns>
        public IServiceCollection AddMsmt()
        {
            services.TryAddSingleton<IMsmtMessagePeer.IFactory, IMsmtMessagePeer.Factory>();
            services.TryAddSingleton<IMsmtSessionPeer.IFactory, IMsmtSessionPeer.Factory>();
            services.TryAddSingleton<IMsmtReachabilityChecker, MsmtReachabilityChecker>();
            return services;
        }
    }
}
