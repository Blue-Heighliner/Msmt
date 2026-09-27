namespace BlueHeighliner.Msmt.Tests.Unit;

/// <summary>Unit tests for <see cref="MsmtServiceCollectionExtensions"/>.</summary>
public sealed class MsmtServiceCollectionExtensionsTests
{
    /// <summary>AddMsmt registers the peer and session peer factories and the reachability checker, each resolving to its default implementation.</summary>
    [Fact]
    public void AddMsmt_RegistersEveryFactory()
    {
        ServiceCollection services = new();

        services.AddMsmt();

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.IsType<IMsmtMessagePeer.Factory>(provider.GetRequiredService<IMsmtMessagePeer.IFactory>());
        Assert.IsType<IMsmtSessionPeer.Factory>(provider.GetRequiredService<IMsmtSessionPeer.IFactory>());
        Assert.IsType<MsmtReachabilityChecker>(provider.GetRequiredService<IMsmtReachabilityChecker>());
    }

    /// <summary>AddMsmt does not overwrite an already-registered factory.</summary>
    [Fact]
    public void AddMsmt_ExistingRegistration_DoesNotOverwrite()
    {
        Mock<IMsmtMessagePeer.IFactory> existing = new();
        ServiceCollection services = new();
        services.AddSingleton(existing.Object);

        services.AddMsmt();

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Same(existing.Object, provider.GetRequiredService<IMsmtMessagePeer.IFactory>());
    }
}
