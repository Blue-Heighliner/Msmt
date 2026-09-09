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

    /// <summary>
    /// Gets the maximum time this client's connection may go without a queued send arriving before it is
    /// automatically disconnected, or <see langword="null"/> to disable this rule and never automatically
    /// disconnect for being idle. Never interrupts a send already queued or in flight - only a fully idle
    /// connection (no sends outstanding) is ever disconnected this way.
    /// </summary>
    public TimeSpan? MaxIdleTime { get; init; } = TimeSpan.FromMinutes(5);
}
