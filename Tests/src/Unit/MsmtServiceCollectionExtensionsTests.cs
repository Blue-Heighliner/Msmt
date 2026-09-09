namespace BlueHeighliner.Msmt.Tests.Unit;

/// <summary>Unit tests for <see cref="MsmtServiceCollectionExtensions"/>.</summary>
public sealed class MsmtServiceCollectionExtensionsTests
{
    /// <summary>AddMsmt registers IMsmtPeerFactory resolving to MsmtPeerFactory.</summary>
    [Fact]
    public void AddMsmt_RegistersMsmtPeerFactory()
    {
        ServiceCollection services = new();

        services.AddMsmt();

        using ServiceProvider provider = services.BuildServiceProvider();
        IMsmtPeerFactory factory = provider.GetRequiredService<IMsmtPeerFactory>();
        Assert.IsType<MsmtPeerFactory>(factory);
    }

    /// <summary>AddMsmt does not overwrite an already-registered IMsmtPeerFactory.</summary>
    [Fact]
    public void AddMsmt_ExistingRegistration_DoesNotOverwrite()
    {
        Mock<IMsmtPeerFactory> existing = new();
        ServiceCollection services = new();
        services.AddSingleton(existing.Object);

        services.AddMsmt();

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Same(existing.Object, provider.GetRequiredService<IMsmtPeerFactory>());
    }
}
