namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// Configuration for connecting to a single remote MSMT server. Used internally by <see cref="MsmtPeer"/>
/// for the connections it creates on demand and for <see cref="MsmtPeer.Test"/> checks.
/// </summary>
internal sealed record MsmtConnectOptions
{
    /// <summary>Gets the remote server to connect to.</summary>
    public required MsmtNameTarget Target { get; init; }

    /// <summary>Gets this client's certificate identity and trusted certificate authorities.</summary>
    public required MsmtCredentials Credentials { get; init; }

    /// <summary>Gets the connection lifecycle mode this client uses. Defaults to <see cref="MsmtOperationMode.Message"/>.</summary>
    public MsmtOperationMode Mode { get; init; } = MsmtOperationMode.Message;

    /// <summary>Gets the maximum TLS connection lifetime this client proposes when negotiating a <see cref="MsmtOperationMode.Session"/> connection.</summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets the number of messages sent via a TLS 1.3 key update before <see cref="MsmtOperationMode.MessageWithRekeying"/> forces a full connection reset.</summary>
    public int RekeyLimit { get; init; } = 3;

    /// <summary>Gets how long the TCP connection attempt and TLS handshake may take, or <see langword="null"/> for no limit.</summary>
    public TimeSpan? HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets how long writing a message may make no progress before the connection is dropped, or <see langword="null"/> for no limit.</summary>
    public TimeSpan? StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets how long to wait for a message's acknowledgement once it is fully written, or <see langword="null"/> for no limit.</summary>
    public TimeSpan? ResponseTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets how long the TCP connection may be silent before the operating system starts probing the remote host, or <see langword="null"/> to leave TCP keep-alive disabled.</summary>
    public TimeSpan? TcpKeepAliveTime { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets the shortest idle time before a <see cref="MsmtOperationMode.Session"/> connection sends a keep-alive.</summary>
    public TimeSpan KeepAliveMinInterval { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Gets the longest idle time before a <see cref="MsmtOperationMode.Session"/> connection sends a keep-alive.</summary>
    public TimeSpan KeepAliveMaxInterval { get; init; } = TimeSpan.FromMinutes(5);
}
