namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// Decides which connections an <see cref="IMsmtMessagePeer"/> should disconnect to enforce <see
/// cref="MsmtMessagePeerOptions.MaxIdleTime"/> and <see cref="MsmtMessagePeerOptions.MaxConnectionCount"/>. Idle limits
/// are judged per connection, so they need nothing but its own activity; the count limit needs a comparison
/// across all of them, so it drops the least recently used idle ones among those left. Never selects one
/// that is in the middle of a message.
/// </summary>
internal interface IMsmtEviction
{
    /// <summary>Selects what to evict now.</summary>
    /// <typeparam name="T">What the owner tracks.</typeparam>
    /// <param name="candidates">Everything the owner currently has.</param>
    /// <returns>Each one to evict, with the reason to report for it and how long it must still be found to have gone unused when the owner re-checks it just before evicting.</returns>
    IReadOnlyList<(T Candidate, Exception Reason, TimeSpan MinIdleTime)> SelectVictims<T>(IReadOnlyCollection<T> candidates)
        where T : IMsmtEvictable;
}

/// <inheritdoc cref="IMsmtEviction" />
/// <param name="maxIdleTime">How long anything may go without traffic, or <see langword="null"/> for no limit.</param>
/// <param name="maxCount">How many may be open at once, or <see langword="null"/> for no limit.</param>
internal sealed class MsmtEviction(TimeSpan? maxIdleTime, int? maxCount) : IMsmtEviction
{
    /// <inheritdoc />
    public IReadOnlyList<(T Candidate, Exception Reason, TimeSpan MinIdleTime)> SelectVictims<T>(IReadOnlyCollection<T> candidates)
        where T : IMsmtEvictable
    {
        List<(T Candidate, Exception Reason, TimeSpan MinIdleTime)> victims = [];
        DateTime now = DateTime.UtcNow;

        if (maxIdleTime is { } idleLimit)
        {
            foreach (T candidate in candidates)
            {
                if (candidate.IsIdle && now - candidate.LastActivityUtc >= idleLimit)
                {
                    victims.Add((candidate, new TimeoutException($"The connection had no application traffic for {idleLimit}."), idleLimit));
                }
            }
        }

        if (maxCount is { } countLimit)
        {
            List<T> remaining = [.. candidates.Where(candidate => !victims.Any(victim => ReferenceEquals(victim.Candidate, candidate)))];
            int excess = remaining.Count - countLimit;

            if (excess > 0)
            {
                foreach (T candidate in remaining.Where(candidate => candidate.IsIdle).OrderBy(candidate => candidate.LastActivityUtc).Take(excess))
                {
                    victims.Add((candidate, new TimeoutException($"The connection was evicted to stay within {countLimit} connections."), TimeSpan.Zero));
                }
            }
        }

        return victims;
    }
}
