namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// One <see cref="IMsmtMessagePeer"/>'s way of sending to one remote target: a queue that outlives any single TLS
/// connection, plus whichever connection the peer's mode calls for right now. In message mode every
/// message gets a connection of its own, which is why the queue can't live on the connection; in
/// message-with-rekeying mode the connection is reused until it reaches the rekey limit.
/// </summary>
internal interface IMsmtTargetSender : IMsmtEvictable, IAsyncDisposable, IDisposable
{
    /// <summary>Gets the remote target this sender sends to.</summary>
    MsmtNameTarget Target { get; }

    /// <summary>Reserves this sender so it can't be evicted before the caller has queued a send on it.</summary>
    /// <returns><see langword="false"/> if it has already been retired, so the caller must look up or create a new one.</returns>
    bool TryReserve();

    /// <summary>Releases a reservation taken by <see cref="TryReserve"/>.</summary>
    void ReleaseReservation();

    /// <summary>Retires this sender if it is idle and has been for at least <paramref name="minIdleTime"/>, after which <see cref="TryReserve"/> refuses.</summary>
    /// <param name="minIdleTime">How long it must have gone unused; <see cref="TimeSpan.Zero"/> for any idle sender.</param>
    /// <returns><see langword="true"/> if it is now retired and the owner should discard it.</returns>
    bool TryRetire(TimeSpan minIdleTime);

    /// <summary>Queues a send.</summary>
    /// <param name="send">The send to queue.</param>
    /// <param name="priority">The send's priority; higher goes first.</param>
    /// <returns><see langword="false"/> if this sender is disposed and did not take the send.</returns>
    bool Enqueue(MsmtPendingSend send, int priority);
}

/// <inheritdoc cref="IMsmtTargetSender" />
internal sealed class MsmtTargetSender : IMsmtTargetSender
{
    /// <summary>Creates a sender and starts its queue.</summary>
    /// <param name="target">The remote target to send to.</param>
    /// <param name="connector">Opens connections to it.</param>
    /// <param name="settings">How the connections it opens behave.</param>
    /// <param name="tracker">Tracks the peer's tagged sends, shared by every sender of the peer.</param>
    /// <param name="packageChanged">Raised as a tagged send's status changes.</param>
    public MsmtTargetSender(MsmtNameTarget target, IMsmtConnector connector, MsmtConnectionSettings settings, IMsmtPackageTracker tracker, Action<MsmtPackageChange> packageChanged)
    {
        Target = target;
        this.connector = connector;
        this.settings = settings;
        outbox = new MsmtOutbox(tracker, () => target, Exchange, packageChanged);
        outbox.Start();
    }

    private readonly IMsmtConnector connector;
    private readonly MsmtConnectionSettings settings;
    private readonly IMsmtOutbox outbox;
    private readonly Lock stateLock = new();
    private readonly int maxClosedRetries = 2;

    private MsmtConnection? current;
    private bool isRetired;

    /// <inheritdoc />
    public MsmtNameTarget Target { get; }

    /// <inheritdoc />
    public bool IsIdle => outbox.IsIdle;

    /// <inheritdoc />
    public DateTime LastActivityUtc => outbox.LastActivityUtc;

    /// <inheritdoc />
    public bool TryReserve()
    {
        lock (stateLock)
        {
            if (isRetired)
            {
                return false;
            }

            outbox.Reserve();
            return true;
        }
    }

    /// <inheritdoc />
    public void ReleaseReservation() => outbox.ReleaseReservation();

    /// <inheritdoc />
    public bool TryRetire(TimeSpan minIdleTime)
    {
        lock (stateLock)
        {
            if (isRetired || !outbox.IsIdle || DateTime.UtcNow - outbox.LastActivityUtc < minIdleTime)
            {
                return isRetired;
            }

            isRetired = true;
            return true;
        }
    }

    /// <inheritdoc />
    public bool Enqueue(MsmtPendingSend send, int priority) => outbox.Enqueue(send, priority);

    /// <inheritdoc />
    public void Dispose()
    {
        outbox.Dispose();
        current?.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await outbox.DisposeAsync();

        if (current is { } connection)
        {
            await connection.DisposeAsync();
        }
    }

    private async Task<MsmtFrame?> Exchange(MsmtPendingSend send, Action<MsmtSendStatus> report, CancellationToken cancellation)
    {
        for (int attempt = 0; ; attempt++)
        {
            MsmtConnection connection = await EnsureConnection(cancellation);

            try
            {
                return await connection.Exchange(send, report, cancellation);
            }
            catch (MsmtConnectionClosedException) when (attempt < maxClosedRetries)
            {
                // The connection finished its own mode's cycle between being chosen and used, before anything
                // was written, so the message can safely go over a new one.
            }
        }
    }

    private async Task<MsmtConnection> EnsureConnection(CancellationToken cancellation)
    {
        if (current is { Status: MsmtConnectionStatus.Connected } connection)
        {
            return connection;
        }

        MsmtConnection opened = await connector.DialAndWait(Target, settings, tracker: null, cancellation);
        current = opened;
        return opened;
    }
}
