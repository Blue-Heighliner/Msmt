namespace BlueHeighliner.Msmt.Tests.Unit;

/// <summary>Unit tests for <see cref="MsmtMemoryOwnerExtensions"/>.</summary>
public sealed class MsmtMemoryOwnerExtensionsTests
{
    /// <summary><see cref="MsmtMemoryOwnerExtensions.Slice"/> exposes exactly the requested range, aliasing the original buffer.</summary>
    [Fact]
    public void Slice_ValidRange_ExposesOnlyRangeOverSameBuffer()
    {
        byte[] buffer = [0, 1, 2, 3, 4, 5];
        Mock<IMemoryOwner<byte>> inner = new();
        inner.Setup(owner => owner.Memory).Returns(buffer);

        IMemoryOwner<byte> sliced = inner.Object.Slice(2, 3);

        Assert.Equal(new byte[] { 2, 3, 4 }, sliced.Memory.ToArray());

        sliced.Memory.Span[0] = 99;
        Assert.Equal(99, buffer[2]);
    }

    /// <summary>Disposing the sliced owner disposes the original exactly once, so the whole buffer returns to its pool.</summary>
    [Fact]
    public void Dispose_SlicedOwner_DisposesOriginalOwner()
    {
        Mock<IMemoryOwner<byte>> inner = new();
        inner.Setup(owner => owner.Memory).Returns(new byte[8]);

        IMemoryOwner<byte> sliced = inner.Object.Slice(0, 4);
        sliced.Dispose();

        inner.Verify(owner => owner.Dispose(), Times.Once);
    }

    /// <summary>A zero-length slice, including at the very end of the buffer, is valid and empty.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Slice_ZeroLength_IsEmpty(int start)
    {
        Mock<IMemoryOwner<byte>> inner = new();
        inner.Setup(owner => owner.Memory).Returns(new byte[4]);

        Assert.True(inner.Object.Slice(start, 0).Memory.IsEmpty);
    }

    /// <summary>A range that is negative or extends past the buffer throws <see cref="ArgumentOutOfRangeException"/>.</summary>
    [Theory]
    [InlineData(-1, 2)]
    [InlineData(0, -1)]
    [InlineData(3, 2)]
    [InlineData(0, 5)]
    [InlineData(5, 0)]
    public void Slice_InvalidRange_Throws(int start, int length)
    {
        Mock<IMemoryOwner<byte>> inner = new();
        inner.Setup(owner => owner.Memory).Returns(new byte[4]);

        Assert.Throws<ArgumentOutOfRangeException>(() => inner.Object.Slice(start, length));
    }

    /// <summary>Slicing a pool-rented buffer that came back larger than requested trims it to exactly the requested length.</summary>
    [Fact]
    public void Slice_OversizedRentedBuffer_TrimsToRequestedLength()
    {
        IMemoryOwner<byte> rented = MemoryPool<byte>.Shared.Rent(10);
        Assert.True(rented.Memory.Length >= 10);

        using IMemoryOwner<byte> sliced = rented.Slice(0, 10);

        Assert.Equal(10, sliced.Memory.Length);
    }
}
