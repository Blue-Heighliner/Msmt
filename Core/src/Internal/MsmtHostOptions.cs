namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// Configuration for an <see cref="MsmtServer"/> listening for MSMT client connections.
/// </summary>
internal sealed record MsmtHostOptions
{
    /// <summary>Gets the local IP address or DNS hostname to listen on. Defaults to <c>0.0.0.0</c> (all interfaces).</summary>
    public string Host { get; init; } = "0.0.0.0";

    /// <summary>Gets the local port to listen on.</summary>
    public required int Port { get; init; }

    /// <summary>Gets this server's certificate identity and trusted certificate authorities used to authenticate connecting clients.</summary>
    public required MsmtCredentials Credentials { get; init; }

    /// <summary>Gets a value indicating whether this server accepts client requests to negotiate a <see cref="MsmtOperationMode.Session"/> connection.</summary>
    public bool SupportsSessionMode { get; init; } = true;

    /// <summary>Gets the cap this server applies to a client's proposed <see cref="MsmtConnectOptions.SessionLifetime"/> when negotiating a <see cref="MsmtOperationMode.Session"/> connection: the agreed lifetime is the lesser of the two.</summary>
    public TimeSpan MaximumSessionLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Gets a value indicating whether this server rejects a connecting client's "server_name" (SNI) value
    /// unless it is a fully qualified DNS hostname, per the ICD, rather than merely a syntactically valid
    /// one (which also accepts IP address literals). Defaults to <see langword="true"/>; disable for testing
    /// or environments without DNS, where connecting by address is otherwise the only option.
    /// </summary>
    public bool RequireFullyQualifiedHostname { get; init; } = true;

    /// <summary>
    /// Gets the maximum time an accepted connection may go without a new message header arriving before it
    /// is automatically disconnected, or <see langword="null"/> to disable this rule and never automatically
    /// disconnect a connection for being idle. Never interrupts a message cycle already in progress - only
    /// the gap between cycles counts as idle, so this has no practical effect on a <see
    /// cref="MsmtOperationMode.Message"/> connection, which the client already closes immediately after
    /// each cycle.
    /// </summary>
    public TimeSpan? MaxIdleTime { get; init; } = TimeSpan.FromMinutes(5);
}
