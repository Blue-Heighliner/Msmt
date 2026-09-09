namespace BlueHeighliner.Msmt.Tests.Unit;

/// <summary>Unit tests for <see cref="MsmtResponder"/> and <see cref="MsmtResponderExtensions"/>.</summary>
public sealed class MsmtResponderTests
{
    /// <summary><see cref="MsmtResponder.Accept()"/> sets <see cref="MsmtResponder.Response"/> to <see cref="MsmtResponseKind.Accept"/> with an empty payload.</summary>
    [Fact]
    public void Accept_NoPayload_SetsResponseToAcceptWithEmptyPayload()
    {
        MsmtResponder responder = new(isResponseRequested: true);

        responder.Accept();

        Assert.Equal(MsmtResponseKind.Accept, responder.Response);
        Assert.Empty(responder.Payload.Memory.ToArray());
    }

    /// <summary><see cref="MsmtResponder.Reject()"/> sets <see cref="MsmtResponder.Response"/> to <see cref="MsmtResponseKind.Reject"/> with an empty payload.</summary>
    [Fact]
    public void Reject_NoPayload_SetsResponseToRejectWithEmptyPayload()
    {
        MsmtResponder responder = new(isResponseRequested: true);

        responder.Reject();

        Assert.Equal(MsmtResponseKind.Reject, responder.Response);
        Assert.Empty(responder.Payload.Memory.ToArray());
    }

    /// <summary><see cref="MsmtResponder.Accept(IMemoryOwner{byte})"/> carries the given payload through to <see cref="MsmtResponder.Payload"/>.</summary>
    [Fact]
    public void Accept_WithPayload_CarriesPayloadThrough()
    {
        MsmtResponder responder = new(isResponseRequested: true);
        byte[] payload = [1, 2, 3];

        responder.Accept(new NonOwningMemoryOwner(payload));

        Assert.Equal(MsmtResponseKind.Accept, responder.Response);
        Assert.Equal(payload, responder.Payload.Memory.ToArray());
    }

    /// <summary><see cref="MsmtResponderExtensions.Accept"/> with a <see cref="ReadOnlyMemory{T}"/> wraps it without copying, mirroring <see cref="IMsmtPeer.Send"/>'s extension.</summary>
    [Fact]
    public void Accept_ReadOnlyMemoryExtension_CarriesPayloadThrough()
    {
        IMsmtResponder responder = new MsmtResponder(isResponseRequested: true);
        byte[] payload = [4, 5, 6];

        responder.Accept((ReadOnlyMemory<byte>)payload);

        Assert.Equal(MsmtResponseKind.Accept, responder.Response);
    }

    /// <summary><see cref="MsmtResponderExtensions.Reject"/> with a <see cref="ReadOnlyMemory{T}"/> wraps it without copying, mirroring <see cref="IMsmtPeer.Send"/>'s extension.</summary>
    [Fact]
    public void Reject_ReadOnlyMemoryExtension_CarriesPayloadThrough()
    {
        IMsmtResponder responder = new MsmtResponder(isResponseRequested: true);
        byte[] payload = [7, 8, 9];

        responder.Reject((ReadOnlyMemory<byte>)payload);

        Assert.Equal(MsmtResponseKind.Reject, responder.Response);
    }

    /// <summary>Calling <see cref="MsmtResponder.Accept()"/> a second time, after a response was already sent, throws.</summary>
    [Fact]
    public void Accept_ResponseAlreadySent_Throws()
    {
        MsmtResponder responder = new(isResponseRequested: true);
        responder.Accept();

        Assert.Throws<InvalidOperationException>(responder.Accept);
    }

    /// <summary>Calling <see cref="MsmtResponder.Reject()"/> after <see cref="MsmtResponder.Accept()"/> already sent a response throws.</summary>
    [Fact]
    public void Reject_ResponseAlreadySent_Throws()
    {
        MsmtResponder responder = new(isResponseRequested: true);
        responder.Accept();

        Assert.Throws<InvalidOperationException>(responder.Reject);
    }

    /// <summary>Calling <see cref="MsmtResponder.Accept()"/> when the sender never requested a response throws.</summary>
    [Fact]
    public void Accept_ResponseNotRequested_Throws()
    {
        MsmtResponder responder = new(isResponseRequested: false);

        Assert.Throws<InvalidOperationException>(responder.Accept);
    }

    /// <summary>Calling <see cref="MsmtResponder.Reject()"/> when the sender never requested a response throws.</summary>
    [Fact]
    public void Reject_ResponseNotRequested_Throws()
    {
        MsmtResponder responder = new(isResponseRequested: false);

        Assert.Throws<InvalidOperationException>(responder.Reject);
    }

    /// <summary>Before either method is called, <see cref="MsmtResponder.Response"/> is <see cref="MsmtResponseKind.None"/>.</summary>
    [Fact]
    public void Response_BeforeAcceptOrReject_IsNone()
    {
        MsmtResponder responder = new(isResponseRequested: true);

        Assert.Equal(MsmtResponseKind.None, responder.Response);
    }

    /// <summary><see cref="MsmtResponder.Defer"/> sets <see cref="MsmtResponder.IsDeferred"/> without deciding <see cref="MsmtResponder.Response"/>.</summary>
    [Fact]
    public void Defer_NotYetDecided_SetsIsDeferredWithoutDeciding()
    {
        MsmtResponder responder = new(isResponseRequested: true);

        responder.Defer();

        Assert.True(responder.IsDeferred);
        Assert.Equal(MsmtResponseKind.None, responder.Response);
    }

    /// <summary>Calling <see cref="MsmtResponder.Defer"/> when the sender never requested a response throws.</summary>
    [Fact]
    public void Defer_ResponseNotRequested_Throws()
    {
        MsmtResponder responder = new(isResponseRequested: false);

        Assert.Throws<InvalidOperationException>(responder.Defer);
    }

    /// <summary>Calling <see cref="MsmtResponder.Defer"/> after a response was already sent throws.</summary>
    [Fact]
    public void Defer_ResponseAlreadySent_Throws()
    {
        MsmtResponder responder = new(isResponseRequested: true);
        responder.Accept();

        Assert.Throws<InvalidOperationException>(responder.Defer);
    }

    /// <summary>After <see cref="MsmtResponder.Defer"/>, <see cref="MsmtResponder.Accept()"/> still decides the response normally.</summary>
    [Fact]
    public void Accept_AfterDefer_StillDecidesResponse()
    {
        MsmtResponder responder = new(isResponseRequested: true);
        responder.Defer();

        responder.Accept();

        Assert.Equal(MsmtResponseKind.Accept, responder.Response);
    }

    /// <summary><see cref="MsmtResponder.WaitForDecision"/> completes only once <see cref="MsmtResponder.Accept()"/> is eventually called, even after the deferring call has already returned.</summary>
    [Fact]
    public async Task WaitForDecision_DeferredThenAcceptedLater_CompletesOnceDecided()
    {
        MsmtResponder responder = new(isResponseRequested: true);
        responder.Defer();

        Task waitTask = responder.WaitForDecision(CancellationToken.None);
        Assert.False(waitTask.IsCompleted);

        responder.Reject();

        await waitTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MsmtResponseKind.Reject, responder.Response);
    }

    /// <summary><see cref="MsmtResponder.WaitForDecision"/> observes cancellation if the response is never decided.</summary>
    [Fact]
    public async Task WaitForDecision_NeverDecided_ObservesCancellation()
    {
        MsmtResponder responder = new(isResponseRequested: true);
        responder.Defer();

        using CancellationTokenSource cancellation = new();
        Task waitTask = responder.WaitForDecision(cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask);
    }
}
