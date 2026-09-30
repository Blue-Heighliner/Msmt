namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// Turns a socket into an <see cref="MsmtConnection"/>, returned immediately and still <see
/// cref="MsmtConnectionStatus.Connecting"/>: the TCP connect (when dialing), TLS handshake, and MSMT's own
/// first exchange all happen in the background, bounded by the handshake timeout, and transition it to
/// <see cref="MsmtConnectionStatus.Connected"/> or <see cref="MsmtConnectionStatus.Disconnected"/>.
/// </summary>
internal interface IMsmtConnector
{
    /// <summary>Starts opening a connection to a remote server.</summary>
    /// <param name="target">The server to connect to.</param>
    /// <param name="settings">How the resulting connection behaves.</param>
    /// <param name="tracker">Tracks the connection's tagged sends, or <see langword="null"/> for a tracker of its own.</param>
    /// <param name="onReceived">Called with a message the remote peer sent, its payload, which it takes ownership of, and an <see cref="IMsmtResponder"/> if the message requested an acknowledgement, or <see langword="null"/> to dispose the payload and accept automatically without reporting it anywhere.</param>
    /// <param name="onPackageChanged">Invoked as a tagged send's status changes.</param>
    /// <param name="onDisconnected">Invoked once, when the connection ends, however it ends - including never having connected at all.</param>
    /// <returns>The connection, still connecting.</returns>
    MsmtConnection Dial(MsmtNameTarget target, MsmtConnectionSettings settings, IMsmtPackageTracker? tracker, Action<MsmtConnection, IMemoryOwner<byte>, IMsmtResponder?>? onReceived = null, Action<MsmtConnection, MsmtPackageChange>? onPackageChanged = null, Action<MsmtConnection, Exception?>? onDisconnected = null);

    /// <summary>Starts establishing a connection over a socket a listener accepted, closing the socket if that fails.</summary>
    /// <param name="socket">The accepted socket, which the connection takes ownership of.</param>
    /// <param name="settings">How the resulting connection behaves.</param>
    /// <param name="tracker">Tracks the connection's tagged sends, or <see langword="null"/> for a tracker of its own.</param>
    /// <param name="onReceived">Called with a message the remote peer sent, its payload, which it takes ownership of, and an <see cref="IMsmtResponder"/> if the message requested an acknowledgement, or <see langword="null"/> to dispose the payload and accept automatically without reporting it anywhere.</param>
    /// <param name="onPackageChanged">Invoked as a tagged send's status changes.</param>
    /// <param name="onDisconnected">Invoked once, when the connection ends, however it ends - including never having connected at all.</param>
    /// <returns>The connection, still connecting.</returns>
    MsmtConnection Accept(Socket socket, MsmtConnectionSettings settings, IMsmtPackageTracker? tracker, Action<MsmtConnection, IMemoryOwner<byte>, IMsmtResponder?>? onReceived = null, Action<MsmtConnection, MsmtPackageChange>? onPackageChanged = null, Action<MsmtConnection, Exception?>? onDisconnected = null);
}

/// <inheritdoc cref="IMsmtConnector" />
/// <param name="options">Supplies the credentials and the handshake, stall, and TCP keep-alive settings.</param>
/// <param name="requireFullyQualifiedHostname">Whether an accepted client's "server_name" must be a fully qualified DNS hostname.</param>
internal sealed class MsmtConnector(MsmtOptions options, bool requireFullyQualifiedHostname) : IMsmtConnector
{
    /// <inheritdoc />
    public MsmtConnection Dial(MsmtNameTarget target, MsmtConnectionSettings settings, IMsmtPackageTracker? tracker, Action<MsmtConnection, IMemoryOwner<byte>, IMsmtResponder?>? onReceived = null, Action<MsmtConnection, MsmtPackageChange>? onPackageChanged = null, Action<MsmtConnection, Exception?>? onDisconnected = null)
    {
        MsmtTarget remote = new() { Host = target.Host, Port = target.Port };
        MsmtConnection connection = new(settings, remote, MsmtConnectionDirection.Outgoing, tracker, onReceived, onPackageChanged, onDisconnected);

        // Attached synchronously, before this method returns, so a caller who immediately disposes the
        // returned connection always has something to abort - no window where the attempt is unreachable.
        Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        MsmtTlsChannel channel = new(socket, options.StallTimeout);
        connection.AttachChannel(channel);

        _ = RunDial(connection, channel, socket, target);
        return connection;
    }

    /// <inheritdoc />
    public MsmtConnection Accept(Socket socket, MsmtConnectionSettings settings, IMsmtPackageTracker? tracker, Action<MsmtConnection, IMemoryOwner<byte>, IMsmtResponder?>? onReceived = null, Action<MsmtConnection, MsmtPackageChange>? onPackageChanged = null, Action<MsmtConnection, Exception?>? onDisconnected = null)
    {
        IPEndPoint endPoint = (IPEndPoint)socket.RemoteEndPoint!;
        MsmtTarget remote = new() { Host = endPoint.Address.ToString(), Port = endPoint.Port };
        MsmtConnection connection = new(settings, remote, MsmtConnectionDirection.Incoming, tracker, onReceived, onPackageChanged, onDisconnected);

        socket.NoDelay = true;
        MsmtTlsChannel channel = new(socket, options.StallTimeout);
        connection.AttachChannel(channel);

        _ = RunAccept(connection, channel, socket);
        return connection;
    }

    private async Task RunDial(MsmtConnection connection, MsmtTlsChannel channel, Socket socket, MsmtNameTarget target)
    {
        using CancellationTokenSource handshake = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectingToken);
        if (options.HandshakeTimeout is { } limit)
        {
            handshake.CancelAfter(limit);
        }

        try
        {
            await socket.ConnectAsync(target.Host, target.Port, handshake.Token);
            MsmtProtocol.ApplyTcpKeepAlive(socket, options.TcpKeepAliveTime);

            MsmtTlsClient tls = new(options.Credentials, target);
            await channel.ConnectAsClient(tls, handshake.Token);

            await connection.CompleteHandshake(tls.ServerIdentity!, handshake.Token);
        }
        catch (Exception exception)
        {
            connection.FailToConnect(Translate(exception, handshake, connection));
        }
    }

    private async Task RunAccept(MsmtConnection connection, MsmtTlsChannel channel, Socket socket)
    {
        using CancellationTokenSource handshake = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectingToken);
        if (options.HandshakeTimeout is { } limit)
        {
            handshake.CancelAfter(limit);
        }

        try
        {
            MsmtProtocol.ApplyTcpKeepAlive(socket, options.TcpKeepAliveTime);

            MsmtTlsServer tls = new(options.Credentials, requireFullyQualifiedHostname);
            await channel.AcceptAsServer(tls, handshake.Token);

            await connection.CompleteHandshake(tls.ClientIdentity!, handshake.Token);
        }
        catch (Exception exception)
        {
            connection.FailToConnect(Translate(exception, handshake, connection));
        }
    }

    // handshake fires either because ConnectingToken was cancelled (the connection was disposed, or the
    // owning peer was) or because CancelAfter's own timer elapsed; only the latter is actually a timeout.
    private Exception Translate(Exception exception, CancellationTokenSource handshake, MsmtConnection connection) =>
        handshake.IsCancellationRequested && !connection.ConnectingToken.IsCancellationRequested && exception is not TimeoutException
            ? new TimeoutException("The connection did not complete its handshake within the configured timeout.", exception)
            : exception;
}

/// <summary>Extension members for <see cref="IMsmtConnector"/>.</summary>
internal static class MsmtConnectorExtensions
{
    extension(IMsmtConnector connector)
    {
        /// <summary>
        /// Dials and waits for the connection to finish establishing, throwing its failure reason instead of
        /// returning a connection that never connected. Restores the awaited, throw-on-failure contract <see
        /// cref="IMsmtConnector.Dial"/> itself no longer offers, for internal callers that don't expose the
        /// connection for a caller to inspect or dispose themselves.
        /// </summary>
        /// <param name="target">The server to connect to.</param>
        /// <param name="settings">How the resulting connection behaves.</param>
        /// <param name="tracker">Tracks the connection's tagged sends, or <see langword="null"/> for a tracker of its own.</param>
        /// <param name="cancellation">Cancels the attempt, disposing the connection.</param>
        /// <param name="onReceived">Called with a message the remote peer sent, its payload, which it takes ownership of, and an <see cref="IMsmtResponder"/> if the message requested an acknowledgement, or <see langword="null"/> to dispose the payload and accept automatically without reporting it anywhere.</param>
        /// <param name="onPackageChanged">Invoked as a tagged send's status changes.</param>
        /// <param name="onDisconnected">Invoked once, when the connection ends, however it ends - including never having connected at all.</param>
        /// <returns>The established connection.</returns>
        /// <exception cref="TimeoutException">The attempt did not complete within the handshake timeout.</exception>
        public Task<MsmtConnection> DialAndWait(MsmtNameTarget target, MsmtConnectionSettings settings, IMsmtPackageTracker? tracker, CancellationToken cancellation, Action<MsmtConnection, IMemoryOwner<byte>, IMsmtResponder?>? onReceived = null, Action<MsmtConnection, MsmtPackageChange>? onPackageChanged = null, Action<MsmtConnection, Exception?>? onDisconnected = null) =>
            EstablishOrThrow(connector.Dial(target, settings, tracker, onReceived, onPackageChanged, onDisconnected), cancellation);

        /// <summary>Accepts and waits for the connection to finish establishing, throwing its failure reason instead of returning a connection that never connected.</summary>
        /// <param name="socket">The accepted socket, which the connection takes ownership of.</param>
        /// <param name="settings">How the resulting connection behaves.</param>
        /// <param name="tracker">Tracks the connection's tagged sends, or <see langword="null"/> for a tracker of its own.</param>
        /// <param name="cancellation">Cancels the attempt, disposing the connection.</param>
        /// <param name="onReceived">Called with a message the remote peer sent, its payload, which it takes ownership of, and an <see cref="IMsmtResponder"/> if the message requested an acknowledgement, or <see langword="null"/> to dispose the payload and accept automatically without reporting it anywhere.</param>
        /// <param name="onPackageChanged">Invoked as a tagged send's status changes.</param>
        /// <param name="onDisconnected">Invoked once, when the connection ends, however it ends - including never having connected at all.</param>
        /// <returns>The established connection.</returns>
        /// <exception cref="TimeoutException">The attempt did not complete within the handshake timeout.</exception>
        public Task<MsmtConnection> AcceptAndWait(Socket socket, MsmtConnectionSettings settings, IMsmtPackageTracker? tracker, CancellationToken cancellation, Action<MsmtConnection, IMemoryOwner<byte>, IMsmtResponder?>? onReceived = null, Action<MsmtConnection, MsmtPackageChange>? onPackageChanged = null, Action<MsmtConnection, Exception?>? onDisconnected = null) =>
            EstablishOrThrow(connector.Accept(socket, settings, tracker, onReceived, onPackageChanged, onDisconnected), cancellation);
    }

    private static async Task<MsmtConnection> EstablishOrThrow(MsmtConnection connection, CancellationToken cancellation)
    {
        bool connected;
        try
        {
            connected = await connection.Wait(cancellation);
        }
        catch (OperationCanceledException)
        {
            connection.Dispose();
            throw;
        }

        if (!connected)
        {
            throw connection.CloseReason ?? new IOException("The connection could not be established.");
        }

        return connection;
    }
}
