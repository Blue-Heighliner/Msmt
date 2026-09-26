namespace BlueHeighliner.Msmt;

/// <summary>
/// Configuration for an <see cref="IMsmtPeer"/>: shared credentials and connection-behavior defaults
/// applied to both the connections it accepts and the connections it creates on demand when sending.
/// </summary>
public sealed record MsmtOptions
{
    /// <summary>Gets this peer's certificate identity and trusted certificate authorities, used both to authenticate connecting peers and to authenticate to peers this one connects out to.</summary>
    public required MsmtCredentials Credentials { get; init; }

    /// <summary>Gets the connection lifecycle mode used for connections this peer creates on demand. Defaults to <see cref="MsmtOperationMode.Message"/>.</summary>
    public MsmtOperationMode Mode { get; init; } = MsmtOperationMode.Message;

    /// <summary>
    /// Gets the maximum TLS connection lifetime this peer proposes when negotiating a <see
    /// cref="MsmtOperationMode.Session"/> connection it creates on demand. Defaults to <see
    /// cref="MaximumSessionLifetime"/>'s own default of 10 minutes, comfortably above the 3-5 minute
    /// randomized interval the background keep-alive check uses, so it has room to keep an idle session
    /// connection open rather than the session simply expiring first.
    /// </summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Gets the number of messages exchanged over one connection, with a TLS 1.3 key update between each,
    /// before <see cref="MsmtOperationMode.MessageWithRekeying"/> forces a full connection reset. Enforced
    /// by both sides: by this peer on connections it creates on demand, and by its listener on accepted
    /// connections that did not negotiate a <see cref="MsmtOperationMode.Session"/>.
    /// </summary>
    public int RekeyLimit { get; init; } = 3;

    /// <summary>Gets a value indicating whether this peer's receiver accepts requests to negotiate a <see cref="MsmtOperationMode.Session"/> connection.</summary>
    public bool SupportsSessionMode { get; init; } = true;

    /// <summary>Gets the cap this peer's receiver applies to a client's proposed session lifetime when negotiating a <see cref="MsmtOperationMode.Session"/> connection: the agreed lifetime is the lesser of the two.</summary>
    public TimeSpan MaximumSessionLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Gets a value indicating whether this peer's receiver rejects a connecting client's "server_name"
    /// (SNI) value unless it is a fully qualified DNS hostname, per the ICD, rather than merely a
    /// syntactically valid one (which also accepts IP address literals). Defaults to <see
    /// langword="true"/>; disable for testing or environments without DNS, where connecting by address is
    /// otherwise the only option.
    /// </summary>
    public bool RequireFullyQualifiedHostname { get; init; } = true;

    /// <summary>
    /// Gets how long a pooled connection - either one this peer created on demand or one its listener
    /// accepted - may go without application traffic before it is automatically disconnected, or <see
    /// langword="null"/> to never disconnect for being unused. Applies in every connection lifecycle mode
    /// and lets a caller who never manages connections by hand rely on the pool dropping the ones it stopped
    /// using. Keep-alives, reachability checks and session negotiation are not application traffic, so a
    /// <see cref="MsmtOperationMode.Session"/> connection kept alive on the wire is still disconnected once
    /// no messages use it. A disconnected on-demand connection's pool entry is discarded too, and a new one
    /// is created transparently by the next send. Never interrupts a message cycle already in progress.
    /// </summary>
    public TimeSpan? MaxIdleTime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets how long a TCP connection attempt and its TLS handshake may take, for connections this peer
    /// creates, accepts, or checks with <see cref="IMsmtPeer.Test"/>, before it is abandoned as a timeout.
    /// Also bounds how long an accepted connection may then take to start sending its first message. <see
    /// langword="null"/> disables this timeout.
    /// </summary>
    public TimeSpan? HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets how long a message transfer may make no progress at all, either direction, before the
    /// connection is dropped as stalled. Restarts on every byte moved, so a large message on a slow but
    /// working link is unaffected. <see langword="null"/> disables this timeout.
    /// </summary>
    public TimeSpan? StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets how long this peer waits for the acknowledgement of a message it sent, measured from the moment
    /// the message was fully written, before the connection is dropped and the send fails with a <see
    /// cref="TimeoutException"/>. Bounds how long the remote application may take to decide, including a
    /// deferred response, and stops one unresponsive peer from blocking every later send to it. <see
    /// langword="null"/> disables this timeout.
    /// </summary>
    public TimeSpan? ResponseTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets how long a TCP connection may be silent before the operating system starts probing whether the
    /// remote host is still there, detecting a peer or network path that vanished without closing the
    /// connection. Best-effort: a platform that rejects the socket options does not fail the connection.
    /// <see langword="null"/> leaves TCP keep-alive disabled.
    /// </summary>
    public TimeSpan? TcpKeepAliveTime { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets the shortest time a <see cref="MsmtOperationMode.Session"/> connection may sit without any message before a keep-alive is sent. Each connection picks a random interval between this and <see cref="KeepAliveMaxInterval"/>, per the ICD.</summary>
    public TimeSpan KeepAliveMinInterval { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Gets the longest time a <see cref="MsmtOperationMode.Session"/> connection may sit without any message before a keep-alive is sent. Must not be less than <see cref="KeepAliveMinInterval"/>.</summary>
    public TimeSpan KeepAliveMaxInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets the maximum number of connections - counted separately for the ones this peer creates on
    /// demand and the ones its listener accepts - to keep open at once. When exceeded, the connection with
    /// the oldest application traffic among those not currently in a message cycle is automatically
    /// disconnected, or <see langword="null"/> to disable this rule and never automatically disconnect a
    /// connection for this reason. Like <see cref="MaxIdleTime"/>, never interrupts a message cycle already
    /// in progress.
    /// </summary>
    public int? MaxConnectionCount { get; init; } = 100;
}
