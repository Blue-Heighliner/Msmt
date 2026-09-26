namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// Bounds how long a single phase of a connection may take while the remote peer owes it something (a
/// handshake, the rest of a message, an acknowledgement), by running a callback - which closes the
/// connection's socket, the only way to interrupt a blocked BouncyCastle handshake or read - if the phase
/// does not finish in time. Only one phase is guarded at a time.
/// </summary>
internal interface IMsmtWatchdog
{
    /// <summary>Gets a value indicating whether a guarded phase has ever run out of time on this watchdog, letting a failure caused by the resulting socket close be reported as a timeout.</summary>
    bool HasExpired { get; }

    /// <summary>
    /// Starts guarding a phase, replacing any phase still being guarded.
    /// </summary>
    /// <param name="timeout">How long the phase may take, or <see langword="null"/> to guard nothing.</param>
    /// <param name="resetOnProgress">Whether each <see cref="NotifyProgress"/> restarts the timeout, so it bounds a stall rather than the phase's total duration.</param>
    /// <returns>A scope that stops guarding the phase when disposed.</returns>
    IDisposable Guard(TimeSpan? timeout, bool resetOnProgress);

    /// <summary>Reports that the remote peer made progress, restarting the current phase's timeout if it was started with <c>resetOnProgress</c>.</summary>
    void NotifyProgress();
}

/// <inheritdoc cref="IMsmtWatchdog" />
/// <param name="onExpired">Runs, at most once per guarded phase, when the phase runs out of time.</param>
internal sealed class MsmtWatchdog(Action onExpired) : IMsmtWatchdog, IDisposable
{
    private readonly Lock stateLock = new();

    private Timer? timer;
    private TimeSpan armedTimeout;
    private bool armedResetsOnProgress;
    private long generation;
    private bool hasExpired;

    /// <inheritdoc />
    public bool HasExpired
    {
        get
        {
            lock (stateLock)
            {
                return hasExpired;
            }
        }
    }

    /// <inheritdoc />
    public IDisposable Guard(TimeSpan? timeout, bool resetOnProgress)
    {
        lock (stateLock)
        {
            timer?.Dispose();
            timer = null;
            long current = ++generation;

            if (timeout is not { } limit)
            {
                return new Scope(this, current);
            }

            armedTimeout = MsmtProtocol.ClampToMaxTimerDuration(limit);
            armedResetsOnProgress = resetOnProgress;
            timer = new Timer(_ => Expire(current), null, armedTimeout, Timeout.InfiniteTimeSpan);
            return new Scope(this, current);
        }
    }

    /// <inheritdoc />
    public void NotifyProgress()
    {
        lock (stateLock)
        {
            if (armedResetsOnProgress)
            {
                timer?.Change(armedTimeout, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>Stops guarding anything, and releases the underlying timer. <see cref="HasExpired"/> stays readable.</summary>
    public void Dispose()
    {
        lock (stateLock)
        {
            generation++;
            timer?.Dispose();
            timer = null;
        }
    }

    private void Expire(long expiredGeneration)
    {
        lock (stateLock)
        {
            if (expiredGeneration != generation)
            {
                return;
            }

            hasExpired = true;
            timer?.Dispose();
            timer = null;
        }

        onExpired();
    }

    private void Release(long releasedGeneration)
    {
        lock (stateLock)
        {
            if (releasedGeneration != generation)
            {
                return;
            }

            generation++;
            timer?.Dispose();
            timer = null;
        }
    }

    private sealed class Scope(MsmtWatchdog watchdog, long generation) : IDisposable
    {
        public void Dispose() => watchdog.Release(generation);
    }
}
