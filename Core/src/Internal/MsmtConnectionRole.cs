namespace BlueHeighliner.Msmt.Internal;

/// <summary>Which side of a connection this endpoint is, which decides who negotiates, keeps alive, and rekeys.</summary>
internal enum MsmtConnectionRole
{
    /// <summary>This side opened the TCP connection and ran the TLS handshake as the client.</summary>
    Initiator,

    /// <summary>This side accepted the TCP connection and ran the TLS handshake as the server.</summary>
    Acceptor,
}
