namespace BlueHeighliner.Msmt.Tests.Unit.Internal;

/// <summary>Unit tests for <see cref="EmptyMemoryOwner"/>.</summary>
public sealed class EmptyMemoryOwnerTests
{
    /// <summary><see cref="EmptyMemoryOwner.Memory"/> is always empty, and disposing it does nothing observable.</summary>
    [Fact]
    public void Instance_MemoryIsEmpty_DisposeDoesNotThrow()
    {
        EmptyMemoryOwner owner = EmptyMemoryOwner.Instance;

        Assert.True(owner.Memory.IsEmpty);
        owner.Dispose();
        owner.Dispose();
    }
}
