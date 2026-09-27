namespace BlueHeighliner.Msmt;

/// <summary>The lifecycle stage of an <see cref="IMsmtConnection"/>.</summary>
public enum MsmtConnectionStatus
{
    /// <summary>The TCP connect, TLS handshake, and session negotiation are still in progress. See <see cref="IMsmtConnection.Wait"/>.</summary>
    Connecting,

    /// <summary>The connection is established and may send and receive messages.</summary>
    Connected,

    /// <summary>The connection has closed, whether because it never finished connecting, or because it later ended.</summary>
    Disconnected,
}
