namespace BlueHeighliner.Msmt.Tests.Unit.Internal;

/// <summary>Unit tests for <see cref="MsmtEviction"/>.</summary>
public sealed class MsmtEvictionTests
{
    /// <summary>Only idle candidates that have gone unused for the idle time are selected.</summary>
    [Fact]
    public void SelectVictims_MaxIdleTime_SelectsOnlyIdleAndStale()
    {
        Candidate stale = new(true, TimeSpan.FromMinutes(10));
        Candidate fresh = new(true, TimeSpan.FromSeconds(1));
        Candidate busyAndStale = new(false, TimeSpan.FromMinutes(10));
        MsmtEviction eviction = new(TimeSpan.FromMinutes(5), null);

        IReadOnlyList<(Candidate Candidate, Exception Reason, TimeSpan MinIdleTime)> victims = eviction.SelectVictims([stale, fresh, busyAndStale]);

        (Candidate candidate, Exception reason, TimeSpan minIdleTime) = Assert.Single(victims);
        Assert.Same(stale, candidate);
        Assert.IsType<TimeoutException>(reason);
        Assert.Equal(TimeSpan.FromMinutes(5), minIdleTime);
    }

    /// <summary>Beyond the count limit, the least recently used idle candidates are selected, never a busy one, and never more than the excess.</summary>
    [Fact]
    public void SelectVictims_MaxCount_SelectsLeastRecentlyUsedIdleUpToTheExcess()
    {
        Candidate oldest = new(true, TimeSpan.FromMinutes(3));
        Candidate older = new(true, TimeSpan.FromMinutes(2));
        Candidate busy = new(false, TimeSpan.FromMinutes(9));
        Candidate newest = new(true, TimeSpan.FromMinutes(1));
        MsmtEviction eviction = new(null, 2);

        IReadOnlyList<(Candidate Candidate, Exception Reason, TimeSpan MinIdleTime)> victims = eviction.SelectVictims([newest, busy, older, oldest]);

        Assert.Equal([oldest, older], victims.Select(victim => victim.Candidate));
        Assert.All(victims, victim => Assert.Equal(TimeSpan.Zero, victim.MinIdleTime));
    }

    /// <summary>A candidate already evicted for being unused counts against the excess only once, so the count limit does not evict more than needed.</summary>
    [Fact]
    public void SelectVictims_IdleVictimsFirst_CountLimitJudgedOnTheRest()
    {
        Candidate stale = new(true, TimeSpan.FromMinutes(10));
        Candidate other = new(true, TimeSpan.FromSeconds(1));
        Candidate another = new(true, TimeSpan.FromSeconds(2));
        MsmtEviction eviction = new(TimeSpan.FromMinutes(5), 2);

        IReadOnlyList<(Candidate Candidate, Exception Reason, TimeSpan MinIdleTime)> victims = eviction.SelectVictims([stale, other, another]);

        Assert.Equal([stale], victims.Select(victim => victim.Candidate));
    }

    /// <summary>With neither limit set, nothing is ever selected.</summary>
    [Fact]
    public void SelectVictims_NoLimits_SelectsNothing() =>
        Assert.Empty(new MsmtEviction(null, null).SelectVictims([new Candidate(true, TimeSpan.FromDays(9))]));

    private sealed class Candidate(bool isIdle, TimeSpan unusedFor) : IMsmtEvictable
    {
        public bool IsIdle { get; } = isIdle;

        public DateTime LastActivityUtc { get; } = DateTime.UtcNow - unusedFor;
    }
}
