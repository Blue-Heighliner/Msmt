namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// Shared wire-level helpers used by connections, listeners, and the connector.
/// </summary>
internal static class MsmtProtocol
{
    /// <summary>
    /// Throws if <paramref name="length"/> exceeds <see cref="MsmtLimits.MaxPayloadLength"/> - the largest
    /// payload an MSMT header can declare and still be considered well-formed by the receiving peer.
    /// Validated before a payload is queued or written, so an oversized one fails fast with a clear error
    /// rather than being transmitted and then rejected as malformed mid-connection.
    /// </summary>
    /// <param name="length">The payload length, in bytes, to validate.</param>
    /// <param name="parameterName">The caller's payload parameter name, reported on the thrown exception.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> exceeds <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
    public static void ValidatePayloadLength(int length, string parameterName)
    {
        if (length > MsmtLimits.MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, length, $"An MSMT payload may be at most {MsmtLimits.MaxPayloadLength} bytes.");
        }
    }

    /// <summary>
    /// Enables TCP keep-alive on <paramref name="socket"/> so the operating system detects a peer or network
    /// path that vanished without closing the connection. Best-effort: a platform that rejects any of the
    /// options leaves the connection unaffected.
    /// </summary>
    /// <param name="socket">The connected socket.</param>
    /// <param name="time">How long the connection may be silent before probing starts, or <see langword="null"/> to do nothing.</param>
    public static void ApplyTcpKeepAlive(Socket socket, TimeSpan? time)
    {
        if (time is not { } keepAliveTime)
        {
            return;
        }

        int seconds = (int)Math.Clamp(Math.Ceiling(keepAliveTime.TotalSeconds), 1, int.MaxValue);

        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, seconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, Math.Min(seconds, 10));
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (Exception exception) when (exception is SocketException or PlatformNotSupportedException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Resolves <paramref name="host"/> to a listenable local address: parsed directly if it's already an
    /// IP address literal, otherwise resolved via DNS.
    /// </summary>
    /// <param name="host">An IP address literal or DNS hostname.</param>
    /// <returns>The resolved address.</returns>
    public static IPAddress ResolveAddress(string host) =>
        IPAddress.TryParse(host, out IPAddress? address) ? address : Dns.GetHostAddresses(host)[0];
}
