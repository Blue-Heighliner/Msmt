namespace BlueHeighliner.Msmt.Tests.Unit.Internal;

/// <summary>Unit tests for <see cref="MsmtPackageTracker"/>.</summary>
public sealed class MsmtPackageTrackerTests
{
    private readonly MsmtNameTarget target = new() { Host = "127.0.0.1", Port = 5000, ServerName = "127.0.0.1" };

    /// <summary>A tracked send starts as <see cref="MsmtSendStatus.Queued"/>, is active, and is found by its tag with its target.</summary>
    [Fact]
    public void Begin_NewTag_IsQueuedAndActive()
    {
        MsmtPackageTracker tracker = new();
        object tag = new();

        tracker.Begin(tag, target, new CancellationTokenSource());

        Assert.Equal(MsmtSendStatus.Queued, tracker.GetStatus(tag));
        IMsmtPackage package = tracker.GetPackage(tag)!;
        Assert.Same(tag, package.Tag);
        Assert.Equal(target, package.Target);
        Assert.Single(tracker.GetActivePackages());
    }

    /// <summary>An unknown tag has no status, no package, and cancelling it does nothing.</summary>
    [Fact]
    public void UnknownTag_HasNothingToReturnOrCancel()
    {
        MsmtPackageTracker tracker = new();
        object tag = new();

        Assert.Null(tracker.GetStatus(tag));
        Assert.Null(tracker.GetPackage(tag));
        Assert.Null(tracker.SetStatus(tag, MsmtSendStatus.Completed));
        tracker.Cancel(tag);
    }

    /// <summary>A finished send is no longer active but stays observable, and can no longer be cancelled.</summary>
    [Fact]
    public void SetStatus_Completed_StaysObservableButInactive()
    {
        MsmtPackageTracker tracker = new();
        object tag = new();
        using CancellationTokenSource cancelSource = new();
        tracker.Begin(tag, target, cancelSource);

        IMsmtPackage? package = tracker.SetStatus(tag, MsmtSendStatus.Completed);
        tracker.Cancel(tag);

        Assert.Equal(MsmtSendStatus.Completed, package!.Status);
        Assert.Equal(MsmtSendStatus.Completed, tracker.GetPackage(tag)!.Status);
        Assert.Empty(tracker.GetActivePackages());
        Assert.False(cancelSource.IsCancellationRequested);
    }

    /// <summary>Cancelling an active send cancels its source, and tolerates the source already being disposed.</summary>
    [Fact]
    public void Cancel_ActiveSend_CancelsItsSourceAndToleratesDisposal()
    {
        MsmtPackageTracker tracker = new();
        object cancelled = new();
        object disposed = new();
        using CancellationTokenSource cancelSource = new();
        CancellationTokenSource disposedSource = new();
        tracker.Begin(cancelled, target, cancelSource);
        tracker.Begin(disposed, target, disposedSource);
        disposedSource.Dispose();

        tracker.Cancel(cancelled);
        tracker.Cancel(disposed);

        Assert.True(cancelSource.IsCancellationRequested);
    }

    /// <summary>A finished send stays observable for five minutes by default, then is forgotten, while a send still active never is.</summary>
    [Fact]
    public void RemoveExpired_FinishedLongerThanRetention_ForgetsOnlyFinishedSends()
    {
        AdjustableTimeProvider time = new();
        MsmtPackageTracker tracker = new(time: time);
        object active = new();
        object finished = new();
        tracker.Begin(active, target, new CancellationTokenSource());
        tracker.Begin(finished, target, new CancellationTokenSource());
        tracker.SetStatus(finished, MsmtSendStatus.Completed);

        time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.Equal(MsmtSendStatus.Completed, tracker.GetStatus(finished));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(tracker.GetStatus(finished));
        Assert.Null(tracker.GetPackage(finished));
        Assert.Equal(MsmtSendStatus.Queued, tracker.GetStatus(active));
    }

    /// <summary>Expiry is timed from when each send finished, not when it began, and cancelled sends expire the same way.</summary>
    [Fact]
    public void RemoveExpired_SendsFinishAtDifferentTimes_ExpireIndependently()
    {
        AdjustableTimeProvider time = new();
        MsmtPackageTracker tracker = new(TimeSpan.FromMinutes(1), time);
        object early = new();
        object late = new();
        tracker.Begin(early, target, new CancellationTokenSource());
        tracker.Begin(late, target, new CancellationTokenSource());
        tracker.SetStatus(early, MsmtSendStatus.Cancelled);

        time.Advance(TimeSpan.FromSeconds(40));
        tracker.SetStatus(late, MsmtSendStatus.Completed);
        time.Advance(TimeSpan.FromSeconds(40));
        tracker.RemoveExpired();

        Assert.Null(tracker.GetStatus(early));
        Assert.Equal(MsmtSendStatus.Completed, tracker.GetStatus(late));
    }

    /// <summary>There is no cap on how many finished sends are tracked within the retention time.</summary>
    [Fact]
    public void SetStatus_ManyFinishedSendsWithinRetention_AllRemembered()
    {
        MsmtPackageTracker tracker = new(time: new AdjustableTimeProvider());
        object[] tags = [.. Enumerable.Range(0, 20_000).Select(_ => new object())];

        foreach (object tag in tags)
        {
            tracker.Begin(tag, target, new CancellationTokenSource());
            tracker.SetStatus(tag, MsmtSendStatus.Completed);
        }

        Assert.All(tags, tag => Assert.Equal(MsmtSendStatus.Completed, tracker.GetStatus(tag)));
    }

    /// <summary>Forgetting an old finished send never forgets a newer send that reused its tag.</summary>
    [Fact]
    public void RemoveExpired_TagReusedByNewerSend_NewerSendIsNotForgotten()
    {
        AdjustableTimeProvider time = new();
        MsmtPackageTracker tracker = new(time: time);
        object reused = new();
        tracker.Begin(reused, target, new CancellationTokenSource());
        tracker.SetStatus(reused, MsmtSendStatus.Completed);
        tracker.Begin(reused, target, new CancellationTokenSource());

        time.Advance(TimeSpan.FromMinutes(6));
        tracker.RemoveExpired();

        Assert.Equal(MsmtSendStatus.Queued, tracker.GetStatus(reused));
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan amount) => now += amount;
    }
}
