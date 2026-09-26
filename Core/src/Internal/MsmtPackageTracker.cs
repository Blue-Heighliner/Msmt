namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// Tracks every tagged send of a peer independently of which pooled <see cref="MsmtClient"/> carried it,
/// so a package stays observable and cancellable after its client has been evicted. Backs <see
/// cref="IMsmtPeer.Packages"/> and <see cref="IMsmtPeer.GetPackage"/>.
/// </summary>
internal interface IMsmtPackageTracker
{
    /// <summary>Starts tracking a tagged send as <see cref="MsmtSendStatus.Queued"/>, replacing any earlier send with the same tag.</summary>
    /// <param name="tag">The tag identifying the send.</param>
    /// <param name="target">The remote peer the send goes to.</param>
    /// <param name="cancelSource">Cancels the send if it has not finished.</param>
    void Begin(object tag, MsmtNameTarget target, CancellationTokenSource cancelSource);

    /// <summary>Records a new status for a tracked send, releasing its cancellation source once the status is final.</summary>
    /// <param name="tag">The tag identifying the send.</param>
    /// <param name="status">The send's new status.</param>
    /// <returns>The package for the send at its new status, or <see langword="null"/> if the tag is not tracked.</returns>
    IMsmtPackage? SetStatus(object tag, MsmtSendStatus status);

    /// <summary>Requests cancellation of a tracked send that has not finished. Does nothing for an unknown or finished tag.</summary>
    /// <param name="tag">The tag identifying the send.</param>
    void Cancel(object tag);

    /// <summary>Forgets every send that finished longer ago than the retention time. Also done lazily by every other member, so an expired tag is never returned.</summary>
    void RemoveExpired();

    /// <summary>Gets the current status of a tracked send.</summary>
    /// <param name="tag">The tag identifying the send.</param>
    /// <returns>The status, or <see langword="null"/> if the tag was never tracked or has been forgotten.</returns>
    MsmtSendStatus? GetStatus(object tag);

    /// <summary>Gets the package for a tracked send.</summary>
    /// <param name="tag">The tag identifying the send.</param>
    /// <returns>The package, or <see langword="null"/> if the tag was never tracked or has been forgotten.</returns>
    IMsmtPackage? GetPackage(object tag);

    /// <summary>Gets a snapshot of every tracked send that has not yet completed or been cancelled.</summary>
    /// <returns>The active packages.</returns>
    IReadOnlyList<IMsmtPackage> GetActivePackages();
}

/// <inheritdoc cref="IMsmtPackageTracker" />
/// <remarks>
/// A finished send is forgotten once it has been finished for the retention time, which bounds growth for a
/// long-lived peer given a uniquely tagged send per message without capping how many it can track.
/// </remarks>
/// <param name="retention">How long a completed or cancelled send stays observable, or <see langword="null"/> for five minutes.</param>
/// <param name="time">The clock used to time retention, or <see langword="null"/> for the system clock.</param>
internal sealed class MsmtPackageTracker(TimeSpan? retention = null, TimeProvider? time = null) : IMsmtPackageTracker
{
    private readonly ConcurrentDictionary<object, Entry> entries = new();
    private readonly ConcurrentQueue<(object Tag, Entry Entry, DateTimeOffset FinishedAt)> finishedEntries = new();
    private readonly Lock removalLock = new();
    private readonly TimeSpan retentionTime = retention ?? TimeSpan.FromMinutes(5);
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    /// <inheritdoc />
    public void Begin(object tag, MsmtNameTarget target, CancellationTokenSource cancelSource)
    {
        RemoveExpired();
        entries[tag] = new Entry(target, cancelSource);
    }

    /// <inheritdoc />
    public IMsmtPackage? SetStatus(object tag, MsmtSendStatus status)
    {
        if (!entries.TryGetValue(tag, out Entry? entry))
        {
            return null;
        }

        entry.Status = status;

        if (status is MsmtSendStatus.Completed or MsmtSendStatus.Cancelled)
        {
            entry.CancelSource = null;
            finishedEntries.Enqueue((tag, entry, clock.GetUtcNow()));
            RemoveExpired();
        }

        return new MsmtPackage(this, tag, entry.Target, status);
    }

    /// <inheritdoc />
    public void Cancel(object tag)
    {
        if (entries.TryGetValue(tag, out Entry? entry) && entry.CancelSource is { } cancelSource)
        {
            try
            {
                cancelSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The send raced to completion and disposed its cancellation source between the lookup
                // above and this call; nothing left to cancel.
            }
        }
    }

    /// <inheritdoc />
    public MsmtSendStatus? GetStatus(object tag)
    {
        RemoveExpired();
        return entries.TryGetValue(tag, out Entry? entry) ? entry.Status : null;
    }

    /// <inheritdoc />
    public IMsmtPackage? GetPackage(object tag)
    {
        RemoveExpired();
        return entries.TryGetValue(tag, out Entry? entry) ? new MsmtPackage(this, tag, entry.Target, entry.Status) : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<IMsmtPackage> GetActivePackages() =>
        [.. entries.Where(pair => pair.Value.Status is not (MsmtSendStatus.Completed or MsmtSendStatus.Cancelled)).Select(pair => new MsmtPackage(this, pair.Key, pair.Value.Target, pair.Value.Status))];

    /// <inheritdoc />
    public void RemoveExpired()
    {
        DateTimeOffset cutoff = clock.GetUtcNow() - retentionTime;

        lock (removalLock)
        {
            while (finishedEntries.TryPeek(out (object Tag, Entry Entry, DateTimeOffset FinishedAt) oldest) && oldest.FinishedAt <= cutoff)
            {
                finishedEntries.TryDequeue(out _);

                // Removed by instance, so a newer send that has since reused the same tag is never forgotten.
                entries.TryRemove(KeyValuePair.Create(oldest.Tag, oldest.Entry));
            }
        }
    }

    private sealed class Entry(MsmtNameTarget target, CancellationTokenSource? initialCancelSource)
    {
        private volatile CancellationTokenSource? cancelSource = initialCancelSource;
        private volatile int status = (int)MsmtSendStatus.Queued;

        public MsmtNameTarget Target { get; } = target;

        public MsmtSendStatus Status
        {
            get => (MsmtSendStatus)status;
            set => status = (int)value;
        }

        public CancellationTokenSource? CancelSource
        {
            get => cancelSource;
            set => cancelSource = value;
        }
    }
}
