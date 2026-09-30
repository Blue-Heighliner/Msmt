namespace BlueHeighliner.Msmt;

/// <summary>
/// What <see cref="IMsmtMessagePeer"/> and <see cref="IMsmtSessionPeer"/> share: a listener, tagged-send
/// tracking, exception reporting, and disposal. Neither is used through this interface directly - it exists so shared behavior
/// isn't declared twice, not as a third, mode-agnostic way to send or receive.
/// </summary>
public interface IMsmtPeer : IDisposable, IAsyncDisposable
{
    /// <summary>Gets an observable that publishes as a tagged send's package progresses, for sends given a non-<see langword="null"/> tag. A send that fails is reported here as <see cref="MsmtSendStatus.Failed"/> only if it was tagged.</summary>
    IObservable<MsmtPackageChange> PackageChanged { get; }

    /// <summary>
    /// Gets an observable that publishes every exception this peer meets while processing something that did
    /// not break a connection, so it would otherwise go unreported: an exception a <c>Receiver</c> throws, a
    /// subscriber to <see cref="PackageChanged"/> or a session peer's <c>Disconnected</c> throwing, the
    /// listener itself failing to accept a client, and, for a message peer, which exposes no connections to
    /// report them on, a connection its listener accepted that failed to establish or a failure evicting
    /// one. An exception that ends a connection is reported through that connection instead, not here.
    /// Publishes synchronously on whichever thread met the exception, and an exception thrown by a
    /// subscriber here is swallowed rather than reported again.
    /// </summary>
    IObservable<Exception> Exceptions { get; }

    /// <summary>Gets the address and port this peer's listener is actually listening on, once <see cref="StartListener"/> has been called, resolving any requested ephemeral port; <see langword="null"/> if not currently listening.</summary>
    MsmtTarget? Listener { get; }

    /// <summary>Gets a snapshot of this peer's currently active (not yet finished) tagged packages, whichever connection carries them.</summary>
    IReadOnlyList<IMsmtPackage> Packages { get; }

    /// <summary>
    /// Binds a listening socket and starts accepting incoming connections in the background. If already
    /// listening, the previous listener is stopped first, as if <see cref="StopListener"/> had been called.
    /// </summary>
    /// <param name="port">The local port to listen on. Defaults to <c>0</c> for an OS-assigned ephemeral port.</param>
    /// <param name="host">The local IP address or DNS hostname to listen on. Defaults to <c>0.0.0.0</c> (all interfaces).</param>
    /// <exception cref="SocketException">The listening socket could not be bound; the peer is left not listening.</exception>
    /// <exception cref="ObjectDisposedException">This peer has been disposed.</exception>
    void StartListener(int port = 0, string host = "0.0.0.0");

    /// <summary>Stops accepting new connections and immediately closes the listener. Connections already open, or already accepted, stay open.</summary>
    void StopListener();

    /// <summary>
    /// Gets the package for a tagged send previously queued with <paramref name="tag"/>. Tracked by the
    /// peer itself, so it keeps working after whichever connection carried the send has been disconnected
    /// and discarded. A completed, cancelled or failed tag's package is forgotten five minutes after the
    /// send finished, so this may also return <see langword="null"/> for a tag whose send finished longer
    /// ago than that.
    /// </summary>
    /// <param name="tag">The tag identifying the send to look up.</param>
    /// <returns>The matching package, or <see langword="null"/> if no send was ever queued with this tag, or it has since been forgotten.</returns>
    IMsmtPackage? GetPackage(object tag);
}

/// <summary>
/// Extension members for <see cref="IMsmtPeer"/>.
/// </summary>
public static class MsmtPeerExtensions
{
    extension(IMsmtPeer peer)
    {
        /// <summary>Gets a value indicating whether <see cref="IMsmtPeer.StartListener"/> last succeeded, and neither <see cref="IMsmtPeer.StopListener"/> nor disposal has happened since.</summary>
        public bool IsListening => peer.Listener is not null;
    }
}
