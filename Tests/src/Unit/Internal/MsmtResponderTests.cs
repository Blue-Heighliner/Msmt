namespace BlueHeighliner.Msmt.Tests.Unit.Internal;

/// <summary>Unit tests for <see cref="MsmtResponder"/> and <see cref="MsmtResponderExtensions"/>.</summary>
public sealed class MsmtResponderTests
{
    /// <summary>Accepting queues a positive acknowledgement with no payload.</summary>
    [Fact]
    public void Accept_NoPayload_SendsSuccessWithNoPayload()
    {
        (bool Success, IMemoryOwner<byte>? Payload)? sent = null;
        MsmtResponder responder = new((success, payload) => sent = (success, payload));

        responder.Accept();

        Assert.Equal((true, null), sent);
    }

    /// <summary>Rejecting queues a negative acknowledgement and hands the payload's ownership on.</summary>
    [Fact]
    public void Reject_WithPayload_SendsFailureWithPayload()
    {
        (bool Success, IMemoryOwner<byte>? Payload)? sent = null;
        MsmtResponder responder = new((success, payload) => sent = (success, payload));
        NonOwningMemoryOwner owner = new(new byte[] { 1, 2, 3 });

        responder.Reject(owner);

        Assert.False(sent!.Value.Success);
        Assert.Same(owner, sent.Value.Payload);
    }

    /// <summary>Only one answer is allowed, and a refused one is not sent.</summary>
    [Fact]
    public void Answer_Twice_Throws()
    {
        int sent = 0;
        MsmtResponder responder = new((success, payload) => sent++);
        responder.Accept();

        Assert.Throws<InvalidOperationException>(() => responder.Reject());
        Assert.Throws<InvalidOperationException>(() => responder.Accept());
        Assert.Equal(1, sent);
    }

    /// <summary>A payload longer than <see cref="MsmtLimits.MaxPayloadLength"/> is refused up front, without using up the answer.</summary>
    [Fact]
    public void Accept_OversizedPayload_ThrowsAndLeavesMessageUnanswered()
    {
        int sent = 0;
        MsmtResponder responder = new((success, payload) => sent++);

        Assert.Throws<ArgumentOutOfRangeException>(() => responder.Accept(new NonOwningMemoryOwner(new byte[MsmtLimits.MaxPayloadLength + 1])));

        responder.Accept();
        Assert.Equal(1, sent);
    }

    /// <summary>The <see cref="ReadOnlyMemory{T}"/> overloads wrap the payload for the underlying responder.</summary>
    [Fact]
    public void ReadOnlyMemoryOverloads_WrapPayload()
    {
        Mock<IMsmtResponder> responder = new();
        byte[]? accepted = null;
        byte[]? rejected = null;
        responder.Setup(mock => mock.Accept(It.IsAny<IMemoryOwner<byte>?>())).Callback<IMemoryOwner<byte>?>(owner => accepted = owner!.Memory.ToArray());
        responder.Setup(mock => mock.Reject(It.IsAny<IMemoryOwner<byte>?>())).Callback<IMemoryOwner<byte>?>(owner => rejected = owner!.Memory.ToArray());

        responder.Object.Accept((ReadOnlyMemory<byte>)new byte[] { 4 });
        responder.Object.Reject((ReadOnlyMemory<byte>)new byte[] { 5 });

        Assert.Equal(new byte[] { 4 }, accepted);
        Assert.Equal(new byte[] { 5 }, rejected);
    }
}
