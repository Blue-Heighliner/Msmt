namespace BlueHeighliner.Msmt;

/// <summary>Configuration for an <see cref="IMsmtMessagePeer"/>.</summary>
public sealed record MsmtMessagePeerOptions : MsmtOptions
{
    /// <summary>
    /// Gets the number of messages exchanged over one connection, with a TLS 1.3 key update between each,
    /// before it is torn down and the next message opens a fresh one. Enforced by both sides: by this peer
    /// on connections it opens, and by its listener on connections it accepts. Defaults to <c>1</c>, which
    /// is Message mode - a brand-new connection for every message, and the most secure, since it never
    /// reuses a TLS session's keys. A value above <c>1</c> is Message mode with rekeying: cheaper than a
    /// full handshake per message, at the cost of more traffic sharing one TLS session between key updates.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is less than <c>1</c>.</exception>
    public int RekeyLimit
    {
        get;
        init
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "RekeyLimit must be at least 1.");
            }

            field = value;
        }
    } = 1;

    /// <summary>
    /// Gets a value indicating whether this peer's listener rejects a connecting client's "server_name"
    /// (SNI) value unless it is a fully qualified DNS hostname, per the ICD, rather than merely a
    /// syntactically valid one (which also accepts IP address literals). Defaults to <see
    /// langword="true"/>; disable for testing or environments without DNS, where connecting by address is
    /// otherwise the only option.
    /// </summary>
    public bool RequireFullyQualifiedHostname { get; init; } = true;

    /// <summary>
    /// Gets how long a connection may go without application traffic before it is automatically
    /// disconnected, or <see langword="null"/> to never disconnect for being unused. Keep-alives,
    /// reachability checks and session negotiation are not application traffic. Never interrupts a message
    /// already in progress. Only <see cref="IMsmtMessagePeer"/> disconnects connections this way; <see
    /// cref="IMsmtSessionPeer"/> leaves connection management entirely to the caller.
    /// </summary>
    public TimeSpan? MaxIdleTime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets the maximum number of connections to keep open at once. When exceeded, the connection with the
    /// oldest application traffic among those not currently exchanging a message is automatically
    /// disconnected, or <see langword="null"/> to never disconnect a connection for this reason. This peer
    /// counts the connections it opens and the ones it accepts separately.
    /// </summary>
    public int? MaxConnectionCount { get; init; } = 100;

    /// <inheritdoc />
    internal override void Validate()
    {
        base.Validate();

        if (MaxIdleTime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxIdleTime), MaxIdleTime, $"{nameof(MaxIdleTime)} must be positive, or null where it can be disabled.");
        }
    }
}
