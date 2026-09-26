namespace BlueHeighliner.Msmt.Tests.Unit.Internal;

/// <summary>Unit tests for <see cref="MsmtWatchdog"/> and <see cref="MsmtWatchedStream"/>.</summary>
public sealed class MsmtWatchdogTests
{
    private readonly TimeSpan shortTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>A guarded phase that outlives its timeout runs the expiry callback once and marks the watchdog expired.</summary>
    [Fact]
    public async Task Guard_PhaseOutlivesTimeout_RunsCallbackAndMarksExpired()
    {
        TaskCompletionSource expired = new();
        int calls = 0;
        using MsmtWatchdog watchdog = new(() =>
        {
            Interlocked.Increment(ref calls);
            expired.TrySetResult();
        });

        using (watchdog.Guard(shortTimeout, false))
        {
            await expired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(watchdog.HasExpired);
        Assert.Equal(1, calls);
    }

    /// <summary>A phase finished before its timeout never runs the callback.</summary>
    [Fact]
    public async Task Guard_PhaseFinishesInTime_NeverExpires()
    {
        int calls = 0;
        using MsmtWatchdog watchdog = new(() => Interlocked.Increment(ref calls));

        using (watchdog.Guard(shortTimeout, false))
        {
        }

        await Task.Delay(shortTimeout * 3);

        Assert.False(watchdog.HasExpired);
        Assert.Equal(0, calls);
    }

    /// <summary>A <see langword="null"/> timeout guards nothing, and also stops guarding the previous phase.</summary>
    [Fact]
    public async Task Guard_NullTimeout_GuardsNothingAndReplacesPreviousPhase()
    {
        int calls = 0;
        using MsmtWatchdog watchdog = new(() => Interlocked.Increment(ref calls));

        using (watchdog.Guard(shortTimeout, false))
        using (watchdog.Guard(null, false))
        {
            await Task.Delay(shortTimeout * 3);
        }

        Assert.False(watchdog.HasExpired);
        Assert.Equal(0, calls);
    }

    /// <summary>Progress restarts a stall timeout, so a phase that keeps moving outlives it, and stops once it goes quiet.</summary>
    [Fact]
    public async Task NotifyProgress_ResetOnProgress_KeepsPhaseAliveUntilItStalls()
    {
        TaskCompletionSource expired = new();
        using MsmtWatchdog watchdog = new(() => expired.TrySetResult());

        using (watchdog.Guard(TimeSpan.FromMilliseconds(300), true))
        {
            for (int index = 0; index < 5; index++)
            {
                await Task.Delay(100);
                watchdog.NotifyProgress();
            }

            Assert.False(watchdog.HasExpired);
            await expired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(watchdog.HasExpired);
    }

    /// <summary>Progress does not extend a phase guarded by a total timeout.</summary>
    [Fact]
    public async Task NotifyProgress_TotalTimeout_DoesNotExtendPhase()
    {
        TaskCompletionSource expired = new();
        using MsmtWatchdog watchdog = new(() => expired.TrySetResult());

        using (watchdog.Guard(TimeSpan.FromMilliseconds(300), false))
        {
            for (int index = 0; index < 5 && !expired.Task.IsCompleted; index++)
            {
                await Task.Delay(100);
                watchdog.NotifyProgress();
            }

            await expired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(watchdog.HasExpired);
    }

    /// <summary>A stale scope disposed after a newer phase began does not disarm the newer phase.</summary>
    [Fact]
    public async Task Guard_StaleScopeDisposedAfterNewerPhase_LeavesNewerPhaseArmed()
    {
        TaskCompletionSource expired = new();
        using MsmtWatchdog watchdog = new(() => expired.TrySetResult());

        IDisposable first = watchdog.Guard(TimeSpan.FromSeconds(30), false);
        using (watchdog.Guard(shortTimeout, false))
        {
            first.Dispose();
            await expired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(watchdog.HasExpired);
    }

    /// <summary>Reads and writes through <see cref="MsmtWatchedStream"/> report progress, but an empty read does not.</summary>
    [Fact]
    public async Task WatchedStream_ReadAndWrite_ReportsProgressOnlyForBytesMoved()
    {
        Mock<IMsmtWatchdog> watchdog = new();
        using MemoryStream inner = new();
        inner.Write([1, 2, 3]);
        inner.Position = 0;
        using MsmtWatchedStream stream = new(inner, watchdog.Object);

        byte[] buffer = new byte[8];
        Assert.Equal(3, await stream.ReadAsync(buffer.AsMemory()));
        Assert.Equal(0, await stream.ReadAsync(buffer.AsMemory()));
        watchdog.Verify(instance => instance.NotifyProgress(), Times.Once);

        stream.Write(buffer, 0, 2);
        await stream.WriteAsync(buffer.AsMemory(0, 2));
        watchdog.Verify(instance => instance.NotifyProgress(), Times.Exactly(3));
    }
}
