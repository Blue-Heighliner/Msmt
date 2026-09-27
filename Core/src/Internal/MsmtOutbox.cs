namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// Queues sends and runs them one at a time, in priority order and first-in-first-out within a priority,
/// so calling code never blocks on the network and only one message is ever in flight. What "send" means
/// is supplied by the owner: a bidirectional connection sends over itself, and an <see cref="IMsmtMessagePeer"/>'s
/// per-target queue opens whatever connection its mode calls for.
/// </summary>
internal interface IMsmtOutbox : IAsyncDisposable, IDisposable
{
    /// <summary>Gets a value indicating whether nothing is queued, in flight, or reserved, so the owner may be evicted without interrupting a send.</summary>
    bool IsIdle { get; }

    /// <summary>Gets the last time an application send completed, not counting keep-alives.</summary>
    DateTime LastActivityUtc { get; }

    /// <summary>Starts processing queued sends.</summary>
    void Start();

    /// <summary>Holds this outbox non-idle until the matching <see cref="ReleaseReservation"/>, so eviction can't dispose it between a caller finding it and queuing on it.</summary>
    void Reserve();

    /// <summary>Releases a hold taken by <see cref="Reserve"/>.</summary>
    void ReleaseReservation();

    /// <summary>Queues a send.</summary>
    /// <param name="send">The send to queue, which is owned by this outbox once queued.</param>
    /// <param name="priority">The send's priority; higher goes first.</param>
    /// <returns><see langword="false"/> if this outbox is disposed and did not take the send.</returns>
    bool Enqueue(MsmtPendingSend send, int priority);
}

/// <inheritdoc cref="IMsmtOutbox" />
/// <param name="tracker">Tracks tagged sends as packages.</param>
/// <param name="target">Gets the remote peer sends go to, reported by their packages.</param>
/// <param name="exchange">Sends one message and returns its acknowledgement, or <see langword="null"/> for one that never requested it, reporting progress through the given callback.</param>
/// <param name="packageChanged">Raised as a tagged send's status changes.</param>
internal sealed class MsmtOutbox(IMsmtPackageTracker tracker, Func<MsmtNameTarget> target, Func<MsmtPendingSend, Action<MsmtSendStatus>, CancellationToken, Task<MsmtFrame?>> exchange, Action<MsmtPackageChange> packageChanged) : IMsmtOutbox
{
    private readonly PriorityQueue<MsmtPendingSend, (int NegatedPriority, long Sequence)> queue = new();
    private readonly Lock queueLock = new();

    // Neither this nor disposalCancellation is ever disposed, and neither holds anything that needs releasing:
    // a racing Enqueue may still release this, and the loop may still read the token after shutdown.
    private readonly SemaphoreSlim queueSignal = new(0);
    private readonly CancellationTokenSource disposalCancellation = new();

    private long sendSequence;
    private Task? processingLoop;
    private long lastActivityTicks = DateTime.UtcNow.Ticks;
    private int outstandingSends;
    private bool isDisposed;

    /// <inheritdoc />
    public bool IsIdle => Volatile.Read(ref outstandingSends) == 0;

    /// <inheritdoc />
    public DateTime LastActivityUtc => new(Interlocked.Read(ref lastActivityTicks), DateTimeKind.Utc);

    /// <inheritdoc />
    public void Start() => processingLoop ??= Task.Run(ProcessQueueLoop);

    /// <inheritdoc />
    public void Reserve() => Interlocked.Increment(ref outstandingSends);

    /// <inheritdoc />
    public void ReleaseReservation() => Interlocked.Decrement(ref outstandingSends);

    /// <inheritdoc />
    public bool Enqueue(MsmtPendingSend send, int priority)
    {
        lock (queueLock)
        {
            if (isDisposed)
            {
                send.CancelSource.Dispose();
                return false;
            }

            if (send.Tag is not null)
            {
                tracker.Begin(send.Tag, target(), send.CancelSource);
            }

            // Counts a send as outstanding until it finishes, so idle eviction never targets an outbox with a
            // send still queued or in flight. Keep-alives are not traffic, so they never count.
            if (!send.IsKeepAlive)
            {
                Interlocked.Increment(ref outstandingSends);
            }

            queue.Enqueue(send, (-priority, sendSequence++));
        }

        // Raised before releasing the queue signal, so the loop can never process (and report further status
        // for) this send before its Queued status has been observed.
        RaisePackageChanged(send.Tag, MsmtSendStatus.Queued, null);
        queueSignal.Release();
        return true;
    }

    /// <summary>Immediately abandons this outbox: cancels everything still queued and stops processing, without waiting for a send in flight.</summary>
    public void Dispose()
    {
        if (!MarkDisposed())
        {
            return;
        }

        disposalCancellation.Cancel();
        DrainQueue();
    }

    /// <summary>Cleanly shuts this outbox down: cancels everything still queued, then waits for the send in flight, if any, to finish.</summary>
    /// <returns>A task that completes once processing has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (!MarkDisposed())
        {
            return;
        }

        await disposalCancellation.CancelAsync();

        if (processingLoop is not null)
        {
            try
            {
                await processingLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        DrainQueue();
    }

    private bool MarkDisposed()
    {
        lock (queueLock)
        {
            if (isDisposed)
            {
                return false;
            }

            isDisposed = true;
            return true;
        }
    }

    private async Task ProcessQueueLoop()
    {
        while (true)
        {
            try
            {
                await queueSignal.WaitAsync(disposalCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                DrainQueue();
                return;
            }

            MsmtPendingSend? send;
            lock (queueLock)
            {
                queue.TryDequeue(out send, out _);
            }

            if (send is not null)
            {
                await Process(send);
            }
        }
    }

    private async Task Process(MsmtPendingSend send)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(send.Cancellation, send.CancelSource.Token, disposalCancellation.Token);
        MsmtSendStatus finalStatus = MsmtSendStatus.Completed;
        Exception? failure = null;
        MsmtFrame? response = null;

        try
        {
            if (linked.IsCancellationRequested)
            {
                throw new OperationCanceledException("The send was cancelled before it started.", linked.Token);
            }

            response = await exchange(send, status => RaisePackageChanged(send.Tag, status, null), linked.Token);

            if (!send.IsKeepAlive)
            {
                Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);
            }
        }
        catch (Exception exception)
        {
            failure = exception;

            // Only an explicit cancel counts: the owner disposing the outbox cancels the linked token too, but a
            // send that fails because its connection closed has failed, not been cancelled.
            bool isCancelled = send.CancelSource.IsCancellationRequested || send.Cancellation.IsCancellationRequested || exception is OperationCanceledException;
            finalStatus = isCancelled ? MsmtSendStatus.Cancelled : MsmtSendStatus.Failed;
        }

        send.Payload.Dispose();
        RaisePackageChanged(send.Tag, finalStatus, finalStatus == MsmtSendStatus.Failed ? failure : null);
        send.CancelSource.Dispose();

        if (!send.IsKeepAlive)
        {
            Interlocked.Decrement(ref outstandingSends);
        }

        // Last, so whoever awaits a request finds its package and the outbox already in their final state.
        Complete(send, response, finalStatus, failure, linked.Token);
    }

    private void Complete(MsmtPendingSend send, MsmtFrame? response, MsmtSendStatus status, Exception? failure, CancellationToken cancellation)
    {
        if (response is { } frame)
        {
            if (send.ResponseSource is { } responseSource)
            {
                bool success = (frame.Header.Flags & MsmtMessageFlags.MessageSuccess) == MsmtMessageFlags.MessageSuccess;
                responseSource.TrySetResult(new MsmtResponse { Success = success, Payload = frame.Payload });
            }
            else
            {
                frame.Payload.Dispose();
            }

            return;
        }

        if (status == MsmtSendStatus.Cancelled)
        {
            send.ResponseSource?.TrySetCanceled((failure as OperationCanceledException)?.CancellationToken ?? cancellation);
        }
        else if (failure is not null)
        {
            send.ResponseSource?.TrySetException(failure);
        }
    }

    private void DrainQueue()
    {
        List<MsmtPendingSend> abandoned;
        lock (queueLock)
        {
            abandoned = [.. queue.UnorderedItems.Select(entry => entry.Element)];
            queue.Clear();
        }

        foreach (MsmtPendingSend send in abandoned)
        {
            send.Payload.Dispose();
            RaisePackageChanged(send.Tag, MsmtSendStatus.Cancelled, null);
            send.ResponseSource?.TrySetCanceled();
            send.CancelSource.Dispose();

            if (!send.IsKeepAlive)
            {
                Interlocked.Decrement(ref outstandingSends);
            }
        }
    }

    private void RaisePackageChanged(object? tag, MsmtSendStatus status, Exception? exception)
    {
        if (tag is not null && tracker.SetStatus(tag, status) is { } package)
        {
            packageChanged(new MsmtPackageChange { Package = package, Status = status, Exception = exception });
        }
    }
}
