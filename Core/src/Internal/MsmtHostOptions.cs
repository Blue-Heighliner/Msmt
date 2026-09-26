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

    /// <summary>Gets the number of messages an accepted connection that did not negotiate a <see cref="MsmtOperationMode.Session"/> serves before this server closes it.</summary>
    public int RekeyLimit { get; init; } = 3;

    /// <summary>Gets how long an accepted connection's TLS handshake, and then the wait for its first message, may take, or <see langword="null"/> for no limit.</summary>
    public TimeSpan? HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets how long reading a message or writing its acknowledgement may make no progress before the connection is dropped, or <see langword="null"/> for no limit.</summary>
    public TimeSpan? StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets how long an accepted TCP connection may be silent before the operating system starts probing the remote host, or <see langword="null"/> to leave TCP keep-alive disabled.</summary>
    public TimeSpan? TcpKeepAliveTime { get; init; } = TimeSpan.FromSeconds(60);
}
