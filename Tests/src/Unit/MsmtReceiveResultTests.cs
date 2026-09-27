namespace BlueHeighliner.Msmt.Tests.Unit;

/// <summary>Unit tests for <see cref="MsmtReceiveResult"/>.</summary>
public sealed class MsmtReceiveResultTests
{
    /// <summary><see cref="MsmtReceiveResult.Accept()"/> is successful with an empty payload.</summary>
    [Fact]
    public void Accept_NoPayload_IsSuccessfulWithNoPayload()
    {
        MsmtReceiveResult result = MsmtReceiveResult.Accept();

        Assert.True(result.Success);
        Assert.Null(result.Payload);
    }

    /// <summary><see cref="MsmtReceiveResult.Reject()"/> is unsuccessful with an empty payload.</summary>
    [Fact]
    public void Reject_NoPayload_IsUnsuccessfulWithNoPayload()
    {
        MsmtReceiveResult result = MsmtReceiveResult.Reject();

        Assert.False(result.Success);
        Assert.Null(result.Payload);
    }

    /// <summary><see cref="MsmtReceiveResult.Accept(IMemoryOwner{byte})"/> carries the given payload through.</summary>
    [Fact]
    public void Accept_WithPayload_CarriesPayloadThrough()
    {
        byte[] payload = [1, 2, 3];

        MsmtReceiveResult result = MsmtReceiveResult.Accept(new NonOwningMemoryOwner(payload));

        Assert.True(result.Success);
        Assert.Equal(payload, result.Payload!.Memory.ToArray());
    }

    /// <summary><see cref="MsmtReceiveResult.Reject(IMemoryOwner{byte})"/> carries the given payload through.</summary>
    [Fact]
    public void Reject_WithPayload_CarriesPayloadThrough()
    {
        byte[] payload = [4, 5, 6];

        MsmtReceiveResult result = MsmtReceiveResult.Reject(new NonOwningMemoryOwner(payload));

        Assert.False(result.Success);
        Assert.Equal(payload, result.Payload!.Memory.ToArray());
    }

    /// <summary><see cref="MsmtReceiveResult.Accept(ReadOnlyMemory{byte})"/> wraps the payload without copying.</summary>
    [Fact]
    public void Accept_ReadOnlyMemoryOverload_CarriesPayloadThrough()
    {
        byte[] payload = [7, 8, 9];

        MsmtReceiveResult result = MsmtReceiveResult.Accept((ReadOnlyMemory<byte>)payload);

        Assert.True(result.Success);
        Assert.Equal(payload, result.Payload!.Memory.ToArray());
    }

    /// <summary><see cref="MsmtReceiveResult.Reject(ReadOnlyMemory{byte})"/> wraps the payload without copying.</summary>
    [Fact]
    public void Reject_ReadOnlyMemoryOverload_CarriesPayloadThrough()
    {
        byte[] payload = [10, 11, 12];

        MsmtReceiveResult result = MsmtReceiveResult.Reject((ReadOnlyMemory<byte>)payload);

        Assert.False(result.Success);
        Assert.Equal(payload, result.Payload!.Memory.ToArray());
    }

    /// <summary>A payload longer than <see cref="MsmtLimits.MaxPayloadLength"/> is rejected up front by both <see cref="MsmtReceiveResult.Accept(IMemoryOwner{byte})"/> and <see cref="MsmtReceiveResult.Reject(IMemoryOwner{byte})"/>.</summary>
    [Fact]
    public void AcceptOrReject_OversizedPayload_Throws()
    {
        NonOwningMemoryOwner oversized = new(new byte[MsmtLimits.MaxPayloadLength + 1]);

        Assert.Throws<ArgumentOutOfRangeException>(() => MsmtReceiveResult.Accept(oversized));
        Assert.Throws<ArgumentOutOfRangeException>(() => MsmtReceiveResult.Reject(oversized));
    }
}
