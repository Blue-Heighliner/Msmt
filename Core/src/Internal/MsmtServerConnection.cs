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

    /// <summary>
    /// Gets a value indicating whether this connection's TLS handshake has completed and the connection is
    /// still open - <see langword="false"/> both before the handshake completes and after the connection
    /// closes, mirroring <see cref="MsmtClient.IsConnected"/>'s symmetric meaning for the sending direction.
    /// </summary>
    internal bool IsConnected { get; private set; }

    /// <summary>
    /// Gets a value indicating whether this connection is currently waiting for its next message header,
    /// as opposed to actively reading, processing, or acknowledging one - <see langword="false"/> during
    /// the handshake and for the duration of every message cycle, and between <see cref="MarkIdle"/> and
    /// <see cref="MarkBusy"/> otherwise. Used by <see cref="MsmtOptions.MaxIdleTime"/>/<see
    /// cref="MsmtOptions.MaxConnectionCount"/> eviction to never interrupt a message cycle in progress.
    /// </summary>
    internal bool IsIdle { get; private set; }

    /// <summary>Gets the last time this connection finished a message cycle and started waiting for the next one.</summary>
    internal DateTime LastActivityUtc => new(Interlocked.Read(ref lastActivityTicks), DateTimeKind.Utc);

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

    /// <summary>Marks this connection as open once its handshake completes.</summary>
    internal void MarkConnected() => IsConnected = true;

    /// <summary>Marks this connection as closed once its accepting loop has finished.</summary>
    internal void MarkDisconnected() => IsConnected = false;

    /// <summary>Marks this connection as waiting for its next message header, recording the current time as its last activity.</summary>
    internal void MarkIdle()
    {
        Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);
        IsIdle = true;
    }

    /// <summary>Marks this connection as no longer idle, since a message header has just been read.</summary>
    internal void MarkBusy() => IsIdle = false;
}
