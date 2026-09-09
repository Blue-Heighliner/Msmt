namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// Pairs up to one cached outgoing <see cref="MsmtClient"/> and one currently accepted incoming <see
/// cref="MsmtServerConnection"/> for the same remote target, backing <see cref="IMsmtPeer.GetActiveConnection"/>
/// and every link's <see cref="IMsmtLink.Connection"/>. Owned and mutated exclusively by <see
/// cref="MsmtPeer"/>, which is responsible for keeping <see cref="SenderEngine"/>/<see
/// cref="ReceiverEngine"/> in sync with its own connection registry.
/// </summary>
/// <param name="target">The remote peer's address and port this connection is with.</param>
internal sealed class MsmtConnection(MsmtTarget target) : IMsmtConnection
{
    private readonly Lock senderLock = new();

    /// <summary>Gets this connection's cached outgoing engine, regardless of whether it is currently mid-session, or <see langword="null"/> if none has been created.</summary>
    internal MsmtClient? SenderEngine { get; private set; }

    /// <summary>Gets this connection's currently accepted incoming link, or <see langword="null"/> if none is currently accepted.</summary>
    internal MsmtServerConnection? ReceiverEngine { get; private set; }

    /// <inheritdoc />
    public MsmtTarget Target { get; } = target;

    /// <inheritdoc />
    public IMsmtLink? Sender => SenderEngine is { IsConnected: true } ? SenderEngine : null;

    /// <inheritdoc />
    public IMsmtLink? Receiver => ReceiverEngine is { IsConnected: true } ? ReceiverEngine : null;

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
    /// name="factory"/> if none exists yet. Guarantees <paramref name="factory"/> runs at most once even
    /// under concurrent callers racing to create the same engine.
    /// </summary>
    /// <param name="factory">Creates and starts a new engine for this connection; invoked at most once.</param>
    /// <returns>The existing or newly created engine.</returns>
    internal MsmtClient EnsureSender(Func<MsmtClient> factory)
    {
        if (SenderEngine is not null)
        {
            return SenderEngine;
        }

        lock (senderLock)
        {
            if (SenderEngine is null)
            {
                MsmtClient client = factory();
                client.AttachConnection(this);
                SenderEngine = client;
            }
        }

        return SenderEngine;
    }

    /// <summary>Attaches an accepted incoming link to this connection, replacing any previous one.</summary>
    /// <param name="link">The incoming link to attach.</param>
    internal void AttachReceiver(MsmtServerConnection link)
    {
        link.AttachConnection(this);
        ReceiverEngine = link;
    }

    /// <summary>Detaches this connection's outgoing engine if it is still <paramref name="client"/>, leaving it unaffected if a newer one has since replaced it.</summary>
    /// <param name="client">The engine expected to still be attached.</param>
    internal void RemoveSender(MsmtClient client)
    {
        if (ReferenceEquals(SenderEngine, client))
        {
            SenderEngine = null;
        }
    }

    /// <summary>Detaches this connection's incoming link if it is still <paramref name="link"/>, leaving it unaffected if a newer one has since replaced it.</summary>
    /// <param name="link">The link expected to still be attached.</param>
    internal void RemoveReceiver(MsmtServerConnection link)
    {
        if (ReferenceEquals(ReceiverEngine, link))
        {
            ReceiverEngine = null;
        }
    }

    /// <summary>Gets a value indicating whether this connection currently has neither a sender nor a receiver engine attached.</summary>
    internal bool IsEmpty => SenderEngine is null && ReceiverEngine is null;
}
