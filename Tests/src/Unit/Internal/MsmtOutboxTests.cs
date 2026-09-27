namespace BlueHeighliner.Msmt.Tests.Unit.Internal;

/// <summary>Unit tests for <see cref="MsmtOutbox"/>, using a fake exchange in place of a connection.</summary>
public sealed class MsmtOutboxTests
{
    private readonly TimeSpan waitLimit = TimeSpan.FromSeconds(10);
    private readonly MsmtNameTarget target = new() { Host = "127.0.0.1", Port = 5000, ServerName = "127.0.0.1" };
    private readonly List<MsmtPackageChange> events = [];

    /// <summary>Sends run one at a time, highest priority first and first-in-first-out within a priority.</summary>
    [Fact]
    public async Task Enqueue_MixedPriorities_RunsOneAtATimeHighestPriorityFirst()
    {
        List<string> order = [];
        TaskCompletionSource release = new();
        int running = 0;
        int maxRunning = 0;
        await using MsmtOutbox outbox = Create(async (send, _, _) =>
        {
            maxRunning = Math.Max(maxRunning, Interlocked.Increment(ref running));
            order.Add(Encoding.ASCII.GetString(send.Payload.Memory.Span));
            if (order.Count == 1)
            {
                await release.Task;
            }

            Interlocked.Decrement(ref running);
            return Acknowledge();
        });
        outbox.Start();

        List<TaskCompletionSource<MsmtResponse>> responses = [];
        foreach ((string name, int priority) in new[] { ("first", 0), ("low-a", 0), ("high", 5), ("mid", 2), ("low-b", 0) })
        {
            TaskCompletionSource<MsmtResponse> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            responses.Add(response);
            Assert.True(outbox.Enqueue(Send(name, response), priority));
            if (name == "first")
            {
                await Task.Delay(100);
            }
        }

        release.SetResult();
        await Task.WhenAll(responses.Select(response => response.Task)).WaitAsync(waitLimit);

        Assert.Equal(["first", "high", "mid", "low-a", "low-b"], order);
        Assert.Equal(1, maxRunning);
    }

    /// <summary>A tagged send reports each status the exchange reports, ending completed, and completes its response with the acknowledgement.</summary>
    [Fact]
    public async Task Enqueue_TaggedSuccess_ReportsStatusesAndCompletesResponse()
    {
        await using MsmtOutbox outbox = Create((_, report, _) =>
        {
            report(MsmtSendStatus.Transmitting);
            report(MsmtSendStatus.PendingAcknowledgement);
            return Task.FromResult<MsmtFrame?>(Acknowledge());
        });
        outbox.Start();
        TaskCompletionSource<MsmtResponse> response = new(TaskCreationOptions.RunContinuationsAsynchronously);

        outbox.Enqueue(Send("hello", response, new object()), 0);

        Assert.True((await response.Task.WaitAsync(waitLimit)).Success);
        await WaitUntil(() => events.Any(args => args.Status == MsmtSendStatus.Completed));
        Assert.Equal([MsmtSendStatus.Queued, MsmtSendStatus.Transmitting, MsmtSendStatus.PendingAcknowledgement, MsmtSendStatus.Completed], events.Select(args => args.Status));
    }

    /// <summary>A failing exchange fails the response and reports a failed status carrying the exception, and later sends still run.</summary>
    [Fact]
    public async Task Enqueue_ExchangeThrows_ReportsFailedWithExceptionAndContinues()
    {
        int calls = 0;
        InvalidOperationException failure = new("boom");
        await using MsmtOutbox outbox = Create((_, _, _) => Interlocked.Increment(ref calls) == 1 ? throw failure : Task.FromResult<MsmtFrame?>(Acknowledge()));
        outbox.Start();
        TaskCompletionSource<MsmtResponse> failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<MsmtResponse> succeeded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        outbox.Enqueue(Send("one", failed, new object()), 0);
        outbox.Enqueue(Send("two", succeeded), 0);

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => failed.Task.WaitAsync(waitLimit)));
        Assert.True((await succeeded.Task.WaitAsync(waitLimit)).Success);
        await WaitUntil(() => events.Any(args => args.Status == MsmtSendStatus.Failed));
        Assert.Same(failure, events.Single(args => args.Status == MsmtSendStatus.Failed).Exception);
    }

    /// <summary>Cancelling a queued send through its tag cancels it without ever running the exchange, and disposes its payload.</summary>
    [Fact]
    public async Task Cancel_QueuedSend_NeverRunsAndIsCancelled()
    {
        TaskCompletionSource release = new();
        List<string> ran = [];
        MsmtPackageTracker tracker = new();
        await using MsmtOutbox outbox = Create(async (send, _, _) =>
        {
            ran.Add(Encoding.ASCII.GetString(send.Payload.Memory.Span));
            await release.Task;
            return Acknowledge();
        }, tracker);
        outbox.Start();
        TaskCompletionSource<MsmtResponse> blocking = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<MsmtResponse> cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<IMemoryOwner<byte>> payload = new();
        payload.Setup(owner => owner.Memory).Returns("cancelled"u8.ToArray());
        object tag = new();

        outbox.Enqueue(Send("blocking", blocking), 0);
        await WaitUntil(() => ran.Count == 1);
        outbox.Enqueue(new MsmtPendingSend { Payload = payload.Object, Flags = MsmtMessageFlags.None, Tag = tag, Cancellation = CancellationToken.None, ResponseSource = cancelled }, 0);
        tracker.Cancel(tag);
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.Task.WaitAsync(waitLimit));
        Assert.Equal(["blocking"], ran);
        Assert.Equal(MsmtSendStatus.Cancelled, tracker.GetStatus(tag));
        payload.Verify(owner => owner.Dispose(), Times.Once);
        Assert.True(outbox.IsIdle || await Task.Run(async () =>
        {
            await WaitUntil(() => outbox.IsIdle);
            return true;
        }));
    }

    /// <summary>Disposing cancels everything still queued and disposes its payloads, and a disposed outbox refuses further sends without taking their payloads.</summary>
    [Fact]
    public async Task Dispose_WithQueuedSends_CancelsThemAndRefusesNewOnes()
    {
        TaskCompletionSource release = new();
        MsmtOutbox outbox = Create(async (_, _, _) =>
        {
            await release.Task;
            return Acknowledge();
        });
        outbox.Start();
        TaskCompletionSource<MsmtResponse> inFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<MsmtResponse> queued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        outbox.Enqueue(Send("one", inFlight), 0);
        await Task.Delay(100);
        outbox.Enqueue(Send("two", queued), 0);

        Task disposing = outbox.DisposeAsync().AsTask();
        release.SetResult();
        await disposing.WaitAsync(waitLimit);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.Task.WaitAsync(waitLimit));
        Mock<IMemoryOwner<byte>> refused = new();
        Assert.False(outbox.Enqueue(new MsmtPendingSend { Payload = refused.Object, Flags = MsmtMessageFlags.None, Cancellation = CancellationToken.None }, 0));
        refused.Verify(owner => owner.Dispose(), Times.Never);
    }

    /// <summary>A reservation, a queued send, and one in flight each keep the outbox from counting as idle; a keep-alive never does and never counts as activity.</summary>
    [Fact]
    public async Task IsIdle_ReservationsAndSends_KeepOutboxBusyExceptKeepAlives()
    {
        TaskCompletionSource release = new();
        await using MsmtOutbox outbox = Create(async (_, _, _) =>
        {
            await release.Task;
            return Acknowledge();
        });
        outbox.Start();
        Assert.True(outbox.IsIdle);

        outbox.Reserve();
        Assert.False(outbox.IsIdle);
        outbox.ReleaseReservation();
        Assert.True(outbox.IsIdle);

        DateTime before = outbox.LastActivityUtc;
        TaskCompletionSource<MsmtResponse> keepAlive = new(TaskCreationOptions.RunContinuationsAsynchronously);
        outbox.Enqueue(new MsmtPendingSend { Payload = EmptyMemoryOwner.Instance, Flags = MsmtMessageFlags.ReachabilityCheck, Cancellation = CancellationToken.None, IsKeepAlive = true, ResponseSource = keepAlive }, 0);
        Assert.True(outbox.IsIdle);
        release.SetResult();
        await keepAlive.Task.WaitAsync(waitLimit);
        await Task.Delay(50);
        Assert.Equal(before, outbox.LastActivityUtc);

        TaskCompletionSource<MsmtResponse> real = new(TaskCreationOptions.RunContinuationsAsynchronously);
        outbox.Enqueue(Send("real", real), 0);
        await real.Task.WaitAsync(waitLimit);
        await WaitUntil(() => outbox.IsIdle);
        Assert.True(outbox.LastActivityUtc > before);
    }

    private MsmtOutbox Create(Func<MsmtPendingSend, Action<MsmtSendStatus>, CancellationToken, Task<MsmtFrame?>> exchange, IMsmtPackageTracker? tracker = null) =>
        new(tracker ?? new MsmtPackageTracker(), () => target, exchange, args =>
        {
            lock (events)
            {
                events.Add(args);
            }
        });

    private MsmtPendingSend Send(string text, TaskCompletionSource<MsmtResponse> response, object? tag = null) => new()
    {
        Payload = new NonOwningMemoryOwner(Encoding.ASCII.GetBytes(text)),
        Flags = MsmtMessageFlags.AcknowledgementRequestedOrGiven,
        Tag = tag,
        Cancellation = CancellationToken.None,
        ResponseSource = response,
    };

    private MsmtFrame Acknowledge() => new(new MsmtHeader { Version = MsmtHeader.SupportedVersion, Flags = MsmtMessageFlags.MessageSuccess, MessageId = 1, Length = 0 }, EmptyMemoryOwner.Instance);

    private async Task WaitUntil(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(waitLimit);
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}
