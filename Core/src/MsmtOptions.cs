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

    /// <summary>Gets the number of messages sent via a TLS 1.3 key update before <see cref="MsmtOperationMode.MessageWithRekeying"/> forces a full connection reset, for connections this peer creates on demand.</summary>
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
    /// Gets the maximum time a connection - either one this peer created on demand or one its listener
    /// accepted - may go between message cycles before it is automatically disconnected, or <see
    /// langword="null"/> to disable this rule and never automatically disconnect a connection for being
    /// idle. Applies independently of connection lifecycle mode, but never interrupts a message cycle
    /// already in progress - only the gap between cycles counts as idle - so it has no practical effect on
    /// a <see cref="MsmtOperationMode.Message"/> connection, which always closes immediately after each
    /// cycle; it can affect a <see cref="MsmtOperationMode.MessageWithRekeying"/> or <see
    /// cref="MsmtOperationMode.Session"/> connection sitting open between messages.
    /// </summary>
    public TimeSpan? MaxIdleTime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets the maximum number of connections - counted separately for the ones this peer creates on
    /// demand and the ones its listener accepts - to keep open at once. When exceeded, the connection with
    /// the oldest activity among those not currently in a message cycle is automatically disconnected, or
    /// <see langword="null"/> to disable this rule and never automatically disconnect a connection for
    /// this reason. Like <see cref="MaxIdleTime"/>, never interrupts a message cycle already in progress.
    /// </summary>
    public int? MaxConnectionCount { get; init; } = 100;
}
