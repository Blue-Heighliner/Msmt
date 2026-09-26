namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// Pairs up to one cached outgoing <see cref="MsmtClient"/> and one currently accepted incoming <see
/// cref="MsmtServerConnection"/> for the same remote target, backing <see cref="IMsmtPeer.GetActiveConnection"/>
/// and every link's <see cref="IMsmtLink.Connection"/>. Owned and mutated exclusively by <see
/// cref="MsmtPeer"/>, which is responsible for keeping <see cref="SenderEngine"/>/<see
/// cref="ReceiverEngine"/> in sync with its own connection registry. Every change to which engines are
/// attached happens under one lock, so a connection can be retired (see <see cref="TryRetire"/>) and removed
/// from that registry without orphaning an engine attached concurrently.
/// </summary>
/// <param name="target">The remote peer's address and port this connection is with.</param>
internal sealed class MsmtConnection(MsmtTarget target) : IMsmtConnection
{
    private readonly Lock stateLock = new();

    private bool isRetired;

    /// <summary>Gets this connection's cached outgoing engine, regardless of whether it is currently mid-session, or <see langword="null"/> if none has been created.</summary>
    internal MsmtClient? SenderEngine { get; private set; }

    /// <summary>Gets this connection's currently accepted incoming link, or <see langword="null"/> if none is currently accepted.</summary>
    internal MsmtServerConnection? ReceiverEngine { get; private set; }

    /// <inheritdoc />
    public MsmtTarget Target { get; } = target;

    /// <inheritdoc />
    public IMsmtLink? Sender => SenderEngine is { IsConnected: true } sender ? sender : null;

    /// <inheritdoc />
    public IMsmtLink? Receiver => ReceiverEngine is { IsConnected: true } receiver ? receiver : null;

    /// <inheritdoc />
    public MsmtIdentity? Identity
    {
        get
        {
            MsmtIdentity? senderIdentity = Sender?.Identity;
            MsmtIdentity? receiverIdentity = Receiver?.Identity;

            if (senderIdentity is null)
            {
                return receiverIdentity;
            }

            if (receiverIdentity is null)
            {
                return senderIdentity;
            }

            return senderIdentity == receiverIdentity ? senderIdentity : null;
        }
    }

    /// <inheritdoc />
    public void Drop()
    {
        SenderEngine?.Drop();
        ReceiverEngine?.Drop();
    }

    /// <inheritdoc />
    public async Task Disconnect()
    {
        Task senderTask = SenderEngine?.Disconnect() ?? Task.CompletedTask;
        Task receiverTask = ReceiverEngine?.Disconnect() ?? Task.CompletedTask;
        await Task.WhenAll(senderTask, receiverTask);
    }

    /// <summary>
    /// Gets this connection's cached outgoing engine, creating and attaching one via <paramref
    /// name="factory"/> if none exists yet, and reserves it (see <see cref="MsmtClient.Reserve"/>) so <see
    /// cref="RemoveIdleSender"/> leaves it attached until the caller releases that reservation. Guarantees
    /// <paramref name="factory"/> runs at most once even under concurrent callers racing to create the same
    /// engine.
    /// </summary>
    /// <param name="factory">Creates and starts a new engine for this connection; invoked at most once.</param>
    /// <returns>The existing or newly created engine, already reserved, or <see langword="null"/> if this connection has been retired.</returns>
    internal MsmtClient? ReserveSender(Func<MsmtClient> factory)
    {
        lock (stateLock)
        {
            if (isRetired)
            {
                return null;
            }

            if (SenderEngine is null)
            {
                MsmtClient client = factory();
                client.AttachConnection(this);
                SenderEngine = client;
            }

            SenderEngine.Reserve();
            return SenderEngine;
        }
    }

    /// <summary>Attaches an accepted incoming link to this connection, replacing any previous one, unless this connection has been retired.</summary>
    /// <param name="link">The incoming link to attach.</param>
    /// <returns><see langword="true"/> if attached; <see langword="false"/> if this connection has been retired.</returns>
    internal bool TryAttachReceiver(MsmtServerConnection link)
    {
        lock (stateLock)
        {
            if (isRetired)
            {
                return false;
            }

            link.AttachConnection(this);
            ReceiverEngine = link;
            return true;
        }
    }

    /// <summary>Detaches this connection's outgoing engine if it is still <paramref name="client"/>, leaving it unaffected if a newer one has since replaced it.</summary>
    /// <param name="client">The engine expected to still be attached.</param>
    internal void RemoveSender(MsmtClient client)
    {
        lock (stateLock)
        {
            if (ReferenceEquals(SenderEngine, client))
            {
                SenderEngine = null;
            }
        }
    }

    /// <summary>Detaches this connection's outgoing engine, but only if it is currently idle and unreserved (see <see cref="MsmtClient.IsIdle"/>), and has been so for at least <paramref name="minIdleTime"/>.</summary>
    /// <param name="minIdleTime">How long the engine must have gone without application traffic; <see cref="TimeSpan.Zero"/> for any idle engine.</param>
    /// <returns>The detached engine, or <see langword="null"/> if none was attached or it was not idle long enough.</returns>
    internal MsmtClient? RemoveIdleSender(TimeSpan minIdleTime)
    {
        lock (stateLock)
        {
            if (SenderEngine is not { IsIdle: true } client || DateTime.UtcNow - client.LastActivityUtc < minIdleTime)
            {
                return null;
            }

            SenderEngine = null;
            return client;
        }
    }

    /// <summary>Detaches this connection's incoming link if it is still <paramref name="link"/>, leaving it unaffected if a newer one has since replaced it.</summary>
    /// <param name="link">The link expected to still be attached.</param>
    internal void RemoveReceiver(MsmtServerConnection link)
    {
        lock (stateLock)
        {
            if (ReferenceEquals(ReceiverEngine, link))
            {
                ReceiverEngine = null;
            }
        }
    }

    /// <summary>
    /// Permanently retires this connection if it has neither engine attached, after which <see
    /// cref="ReserveSender"/> and <see cref="TryAttachReceiver"/> refuse to attach anything, so its owner can
    /// safely remove it from its registry.
    /// </summary>
    /// <returns><see langword="true"/> if this connection is now retired.</returns>
    internal bool TryRetire()
    {
        lock (stateLock)
        {
            if (SenderEngine is null && ReceiverEngine is null)
            {
                isRetired = true;
            }

            return isRetired;
        }
    }
}
