namespace BlueHeighliner.Msmt.Internal;

/// <summary>What an <see cref="MsmtConnection"/> needs to know about how it is to behave, decided by whichever public type creates it.</summary>
internal sealed record MsmtConnectionSettings
{
    /// <summary>Gets which side of the connection this is.</summary>
    public required MsmtConnectionRole Role { get; init; }

    /// <summary>Gets the mode an initiator runs in. An acceptor learns its mode from whether the initiator negotiates a session.</summary>
    public required MsmtConnectionMode Mode { get; init; }

    /// <summary>Gets a value indicating whether messages the remote side sends are delivered to the application. <see langword="false"/> for a connection only ever used to send, such as a peer's outgoing one.</summary>
    public bool AcceptsRequests { get; init; }

    /// <summary>Gets a value indicating whether an acceptor rejects a session negotiation, as a peer's listener does.</summary>
    public bool RejectsSession { get; init; }

    /// <summary>Gets a value indicating whether an acceptor rejects a connection that does not negotiate a session, as a server does.</summary>
    public bool RequiresSession { get; init; }

    /// <summary>Gets how long the handshake, and an acceptor's wait for a first message, may take.</summary>
    public TimeSpan? HandshakeTimeout { get; init; }

    /// <summary>Gets how long a transfer may make no progress before the connection is dropped.</summary>
    public TimeSpan? StallTimeout { get; init; }

    /// <summary>Gets how long an initiator waits for an acknowledgement.</summary>
    public TimeSpan? ResponseTimeout { get; init; }

    /// <summary>Gets the number of messages a non-session connection serves before it closes; <c>1</c> gives Message mode, a higher value Message mode with rekeying.</summary>
    public int RekeyLimit { get; init; } = 1;

    /// <summary>Gets the lifetime an initiator proposes for a session.</summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets the longest session lifetime an acceptor agrees to.</summary>
    public TimeSpan MaximumSessionLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets the shortest silence before an initiator's session sends a keep-alive.</summary>
    public TimeSpan KeepAliveMinInterval { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Gets the longest silence before an initiator's session sends a keep-alive.</summary>
    public TimeSpan KeepAliveMaxInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets a value indicating whether this connection starts its own send queue once connected. <see langword="false"/> for one an owner such as <see cref="MsmtTargetSender"/> drives itself by calling <see cref="MsmtConnection.Exchange"/> directly.</summary>
    public bool ProcessSends { get; init; } = true;
}
