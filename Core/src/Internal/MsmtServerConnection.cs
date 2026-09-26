namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// A single client connection accepted by an <see cref="MsmtServer"/>. Created as soon as a client
/// begins its handshake, before its identity is known.
/// </summary>
/// <param name="target">The connecting client's observed address and port.</param>
internal sealed class MsmtServerConnection(MsmtTarget target) : IMsmtLink
{
    private Action? close;
    private Func<Task>? disconnect;
    private long lastActivityTicks;
    private Exception? closeReason;
    private volatile bool hasCloseReason;
    private volatile bool isConnected;
    private volatile bool isIdle;

    /// <summary>
    /// Gets a value indicating whether this connection's TLS handshake has completed and the connection is
    /// still open - <see langword="false"/> both before the handshake completes and after the connection
    /// closes, mirroring <see cref="MsmtClient.IsConnected"/>'s symmetric meaning for the sending direction.
    /// </summary>
    internal bool IsConnected => isConnected;

    /// <summary>
    /// Gets a value indicating whether this connection is currently waiting for its next message header,
    /// as opposed to actively reading, processing, or acknowledging one - <see langword="false"/> during
    /// the handshake and for the duration of every message cycle, and between <see cref="MarkIdle"/> and
    /// <see cref="MarkBusy"/> otherwise. Used by <see cref="MsmtOptions.MaxIdleTime"/>/<see
    /// cref="MsmtOptions.MaxConnectionCount"/> eviction and the session lifetime backstop to never
    /// interrupt a message cycle in progress.
    /// </summary>
    internal bool IsIdle => isIdle;

    /// <summary>
    /// Gets the last time this connection completed its handshake or finished acknowledging an application
    /// message. Reachability checks and session negotiation are not application traffic and don't count, so
    /// a client that only sends keep-alives still ages toward <see cref="MsmtOptions.MaxIdleTime"/>.
    /// </summary>
    internal DateTime LastActivityUtc => new(Interlocked.Read(ref lastActivityTicks), DateTimeKind.Utc);

    /// <summary>Gets a value indicating whether the server deliberately closed this connection, in which case <see cref="CloseReason"/> is what it reports instead of the read failure the close causes.</summary>
    internal bool HasCloseReason => hasCloseReason;

    /// <summary>Gets why the server deliberately closed this connection, or <see langword="null"/> if it closed it normally.</summary>
    internal Exception? CloseReason => closeReason;

    /// <summary>Gets the connecting client's observed address and port, used to attach this link to its owning <see cref="MsmtConnection"/>.</summary>
    internal MsmtTarget Target { get; } = target;

    /// <inheritdoc />
    public MsmtLinkType Kind => MsmtLinkType.Receiver;

    /// <inheritdoc />
    public MsmtIdentity? Identity { get; private set; }

    /// <inheritdoc />
    public IMsmtConnection Connection { get; private set; } = null!;

    /// <summary>Immediately closes and discards this connection's underlying socket without waiting.</summary>
    public void Drop() => close?.Invoke();

    /// <summary>Cleanly closes this connection, waiting for its accepting loop to finish.</summary>
    /// <returns>A task that completes once the shutdown has finished.</returns>
    public Task Disconnect() => disconnect?.Invoke() ?? Task.CompletedTask;

    /// <summary>Attaches the connection this link belongs to.</summary>
    /// <param name="connection">This link's owning connection.</param>
    internal void AttachConnection(IMsmtConnection connection) => Connection = connection;

    /// <summary>Attaches the callback this connection invokes to immediately close its underlying socket once <see cref="Drop"/> is called.</summary>
    /// <param name="close">Closes this connection's underlying socket.</param>
    internal void AttachCloser(Action close) => this.close = close;

    /// <summary>Attaches the callback this connection invokes to close its underlying socket and await its accepting loop's completion once <see cref="Disconnect"/> is called.</summary>
    /// <param name="disconnect">Closes this connection's underlying socket and awaits its accepting loop.</param>
    internal void AttachDisconnector(Func<Task> disconnect) => this.disconnect = disconnect;

    /// <summary>Records the identity extracted from the connecting client's verified certificate once its handshake completes.</summary>
    /// <param name="identity">The extracted identity.</param>
    internal void SetIdentity(MsmtIdentity identity) => Identity = identity;

    /// <summary>Marks this connection as open once its handshake completes, counting that as its first activity.</summary>
    internal void MarkConnected()
    {
        Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);
        isConnected = true;
    }

    /// <summary>Immediately closes this connection's socket on the server's own initiative.</summary>
    /// <param name="reason">Reported as the connection's disconnection exception, or <see langword="null"/> for a normal close.</param>
    internal void CloseWith(Exception? reason)
    {
        closeReason = reason;
        hasCloseReason = true;
        close?.Invoke();
    }

    /// <summary>Cleanly closes this connection on the server's own initiative, waiting for its accepting loop to finish.</summary>
    /// <param name="reason">Reported as the connection's disconnection exception.</param>
    /// <returns>A task that completes once the connection has closed.</returns>
    internal Task Evict(Exception reason)
    {
        closeReason = reason;
        hasCloseReason = true;
        return Disconnect();
    }

    /// <summary>Marks this connection as closed once its accepting loop has finished.</summary>
    internal void MarkDisconnected() => isConnected = false;

    /// <summary>Marks this connection as waiting for its next message header.</summary>
    internal void MarkIdle() => isIdle = true;

    /// <summary>Records that this connection just finished acknowledging an application message.</summary>
    internal void MarkActivity() => Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);

    /// <summary>Marks this connection as no longer idle, since a message header has just been read.</summary>
    internal void MarkBusy() => isIdle = false;
}
