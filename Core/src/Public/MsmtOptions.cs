namespace BlueHeighliner.Msmt;

/// <summary>
/// Configuration shared by everything that opens or accepts MSMT connections: <see cref="IMsmtMessagePeer"/>
/// and <see cref="IMsmtSessionPeer"/> extend it with their own settings, and <see
/// cref="IMsmtReachabilityChecker.Reach"/> takes it directly, unextended.
/// </summary>
public record MsmtOptions
{
    /// <summary>Gets this endpoint's certificate identity and trusted certificate authorities, used both to authenticate connecting peers and to authenticate to peers it connects out to.</summary>
    public required MsmtCredentials Credentials { get; init; }

    /// <summary>
    /// Gets how long a TCP connection attempt and its TLS handshake may take, for connections opened or
    /// accepted, before it is abandoned as a timeout. Also bounds how long an accepted connection may then
    /// take to send its first message. <see langword="null"/> disables this timeout.
    /// </summary>
    public TimeSpan? HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets how long a message transfer may make no progress at all, in either direction, before the
    /// connection is dropped as stalled. Restarts on every byte moved, so a large message on a slow but
    /// working link is unaffected. <see langword="null"/> disables this timeout.
    /// </summary>
    public TimeSpan? StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets how long to wait for the acknowledgement of a message sent, measured from the moment it was
    /// fully written, before the connection is dropped and the send fails with a <see
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

    /// <summary>Throws if any timeout or interval in these options is not positive.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A timeout or interval is not positive.</exception>
    internal virtual void Validate()
    {
        foreach ((string name, TimeSpan? value) in new (string, TimeSpan?)[]
        {
            (nameof(HandshakeTimeout), HandshakeTimeout),
            (nameof(StallTimeout), StallTimeout),
            (nameof(ResponseTimeout), ResponseTimeout),
            (nameof(TcpKeepAliveTime), TcpKeepAliveTime),
        })
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(name, value, $"{name} must be positive, or null where it can be disabled.");
            }
        }
    }
}
