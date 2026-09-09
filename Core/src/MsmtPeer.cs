namespace BlueHeighliner.Msmt;

/// <summary>
/// A peer-to-peer MSMT API: internally manages one listener for incoming connections and a separate
/// outgoing connection per remote target sent to, created on demand. Both directions are automatically
/// disconnected in the background, independently of each other, once idle or once too many of that
/// direction are open at once.
/// </summary>
public interface IMsmtPeer : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Gets an observable that publishes whenever one of this peer's links begins attempting a connection,
    /// whether incoming (the listener accepting a client) or outgoing (this peer connecting to a remote
    /// target). The carried link's <see cref="IMsmtLink.Identity"/> has not yet been updated for this
    /// attempt - it is <see langword="null"/> for a first attempt, or still reflects a previous attempt's
    /// identity for a reconnect.
    /// </summary>
    IObservable<MsmtLinkingEventArgs> Linking { get; }

    /// <summary>Gets an observable that publishes whenever one of this peer's links establishes a connection, whether incoming (the listener accepting a client) or outgoing (this peer connecting to a remote target), once its handshake has fully completed and been secured.</summary>
    IObservable<MsmtLinkedEventArgs> Linked { get; }

    /// <summary>
    /// Gets an observable that publishes whenever one of this peer's links fails to connect before
    /// completing its handshake, carrying the exception that caused the failure. As with <see
    /// cref="Linking"/>, the carried link's <see cref="IMsmtLink.Identity"/> has not been updated for the
    /// failed attempt.
    /// </summary>
    IObservable<MsmtLinkFailedEventArgs> LinkFailed { get; }

    /// <summary>
    /// Gets an observable that publishes whenever this peer's listener accepts an application message
    /// another peer sent it via <see cref="Send"/> or <see cref="Request"/>. If <see
    /// cref="MsmtReceivedEventArgs.IsResponseRequested"/> is <see langword="true"/>, a subscriber may
    /// acknowledge it through <see cref="MsmtReceivedEventArgs.Responder"/>; if none does, it is accepted
    /// automatically once every subscriber has run, unless a subscriber called <see
    /// cref="IMsmtResponder.Defer"/>, in which case it stays unacknowledged until <see
    /// cref="IMsmtResponder.Accept()"/> or <see cref="IMsmtResponder.Reject()"/> is eventually called.
    /// </summary>
    IObservable<MsmtReceivedEventArgs> Received { get; }

    /// <summary>Gets an observable that publishes whenever one of this peer's established links closes, carrying the link that closed and, if applicable, the exception that caused it.</summary>
    IObservable<MsmtUnlinkedEventArgs> Unlinked { get; }

    /// <summary>Gets an observable that publishes as a tagged send's package progresses, for sends given a non-<see langword="null"/> tag via <see cref="Send"/> or <see cref="Request"/>.</summary>
    IObservable<MsmtPackageChangedEventArgs> PackageChanged { get; }

    /// <summary>
    /// Gets an observable that publishes whenever one of this peer's connections gains its first link - its
    /// <see cref="IMsmtConnection.Sender"/> or <see cref="IMsmtConnection.Receiver"/> becomes non-<see
    /// langword="null"/> while the other is still <see langword="null"/>. Unlike <see cref="Linked"/>,
    /// which fires per link, this fires at most once per connection while it holds at least one link -
    /// e.g. it does not fire again when a connection's second link also becomes active.
    /// </summary>
    IObservable<MsmtConnectedEventArgs> Connected { get; }

    /// <summary>
    /// Gets an observable that publishes whenever one of this peer's connections loses its last link - both
    /// its <see cref="IMsmtConnection.Sender"/> and <see cref="IMsmtConnection.Receiver"/> become <see
    /// langword="null"/>. Unlike <see cref="Unlinked"/>, which fires per link, this fires only once a
    /// connection holds no links at all.
    /// </summary>
    IObservable<MsmtDisconnectedEventArgs> Disconnected { get; }

    /// <summary>Gets the address and port this peer's listener is actually listening on, once <see cref="StartListener"/> has been called, resolving any requested ephemeral port; <see langword="null"/> if not currently listening.</summary>
    MsmtTarget? Listener { get; }

    /// <summary>
    /// Gets a snapshot of this peer's currently active connections - each has raised <see
    /// cref="Connected"/> but not yet <see cref="Disconnected"/>. A connection is added to this list
    /// before <see cref="Connected"/> is raised for it, so the list is already up to date when a <see
    /// cref="Connected"/> subscriber runs.
    /// </summary>
    IReadOnlyList<IMsmtConnection> ActiveConnections { get; }

    /// <summary>Gets a snapshot of this peer's currently active (not yet completed or cancelled) tagged packages, across every outgoing connection.</summary>
    IReadOnlyList<IMsmtPackage> Packages { get; }

    /// <summary>
    /// Binds a listening socket and starts accepting incoming connections in the background. If already
    /// listening, the previous listener is stopped first, as if <see cref="StopListener"/> had been called.
    /// </summary>
    /// <param name="port">The local port to listen on. Defaults to <c>0</c> for an OS-assigned ephemeral port.</param>
    /// <param name="host">The local IP address or DNS hostname to listen on. Defaults to <c>0.0.0.0</c> (all interfaces).</param>
    void StartListener(int port = 0, string host = "0.0.0.0");

    /// <summary>Stops accepting new connections and immediately closes the listener.</summary>
    void StopListener();

    /// <summary>
    /// Queues a message for delivery to <paramref name="target"/>, creating and caching a new outgoing
    /// connection to it if one isn't already open, without requesting or waiting for a response. This
    /// method only enqueues the payload; it does not wait for the send to complete. Observe a queued send's
    /// progress through <see cref="PackageChanged"/>. Use <see cref="Request"/> instead to await the remote
    /// peer's acknowledgement.
    /// </summary>
    /// <param name="target">The remote peer to send to; also used to identify the cached connection.</param>
    /// <param name="payload">
    /// The application message content, rented from a pool (e.g. <see cref="MemoryPool{T}.Shared"/>) so
    /// sending doesn't require an allocation per message. If this method returns without throwing,
    /// ownership of <paramref name="payload"/> transfers to this peer, which disposes it once the send
    /// completes, successfully or not; if it throws, the caller retains ownership.
    /// </param>
    /// <param name="options">Options governing how this payload is sent, or <see langword="null"/> to use the defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
    void Send(MsmtNameTarget target, IMemoryOwner<byte> payload, MsmtSendOptions? options = null);

    /// <summary>
    /// Queues a message for delivery to <paramref name="target"/>, creating and caching a new outgoing
    /// connection to it if one isn't already open, and asynchronously awaits the remote peer's
    /// acknowledgement. Observe a queued request's progress through <see cref="PackageChanged"/>.
    /// </summary>
    /// <param name="target">The remote peer to send to; also used to identify the cached connection.</param>
    /// <param name="payload">
    /// The application message content, rented from a pool (e.g. <see cref="MemoryPool{T}.Shared"/>) so
    /// sending doesn't require an allocation per message. If this method returns without throwing,
    /// ownership of <paramref name="payload"/> transfers to this peer, which disposes it once the request
    /// completes, successfully or not; if it throws, the caller retains ownership.
    /// </param>
    /// <param name="options">Options governing how this payload is sent, or <see langword="null"/> to use the defaults.</param>
    /// <param name="cancellation">Cancels the request before it completes.</param>
    /// <returns>The remote peer's acknowledgement.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
    Task<MsmtResponse> Request(MsmtNameTarget target, IMemoryOwner<byte> payload, MsmtSendOptions? options = null, CancellationToken cancellation = default);

    /// <summary>
    /// Gets the package for the tagged send previously queued via <see cref="Send"/> or <see cref="Request"/>
    /// with <paramref name="tag"/>, across every outgoing connection. A completed or cancelled tag's package
    /// is retained only for a bounded number of the most recently finished sends per connection, so this may
    /// also return <see langword="null"/> for a tag whose send finished long enough ago to have been
    /// forgotten.
    /// </summary>
    /// <param name="tag">The tag identifying the send to look up, as passed to <see cref="Send"/> or <see cref="Request"/>.</param>
    /// <returns>The matching package, or <see langword="null"/> if no send was ever queued with this tag, or it has since been forgotten.</returns>
    IMsmtPackage? GetPackage(object tag);

    /// <summary>
    /// Establishes a connection to <paramref name="target"/> and exchanges a specially-flagged test message
    /// that is never delivered to the remote peer's application logic, to verify it is reachable and
    /// correctly configured.
    /// </summary>
    /// <param name="target">The remote peer to check.</param>
    /// <param name="cancellation">Cancels the check.</param>
    /// <returns><see langword="true"/> if the remote peer responded successfully.</returns>
    Task<bool> Test(MsmtNameTarget target, CancellationToken cancellation = default);

    /// <summary>
    /// Gets this peer's connection with <paramref name="target"/>, matched by address and port alone
    /// (ignoring server name), but only while it holds exactly one linked link. The returned connection's
    /// <see cref="IMsmtConnection.Sender"/> reflects this peer's own outgoing link to that target if that
    /// is the linked one; its <see cref="IMsmtConnection.Receiver"/> reflects an incoming link its
    /// listener has accepted from that same address and port if that is the linked one instead.
    /// </summary>
    /// <param name="target">The remote peer to look up.</param>
    /// <returns>
    /// The matching connection, or <see langword="null"/> if no connection for this target currently
    /// exists, or it exists but does not currently hold exactly one linked link - neither <see
    /// cref="IMsmtConnection.Sender"/> nor <see cref="IMsmtConnection.Receiver"/> is linked yet, or both
    /// are at once.
    /// </returns>
    IMsmtConnection? GetActiveConnection(MsmtTarget target);
}

/// <summary>
/// Extension members for <see cref="IMsmtPeer"/>.
/// </summary>
public static class MsmtPeerExtensions
{
    extension(IMsmtPeer peer)
    {
        /// <summary>Gets a value indicating whether <see cref="IMsmtPeer.StartListener"/> has been called without a matching <see cref="IMsmtPeer.StopListener"/>.</summary>
        public bool IsListening => peer.Listener is not null;

        /// <summary>
        /// Queues a message for delivery to <paramref name="target"/>, wrapping <paramref name="payload"/>
        /// in a non-pooled <see cref="IMemoryOwner{T}"/> so callers with an ordinary <see
        /// cref="ReadOnlyMemory{T}"/> don't need to manage one themselves to call <see cref="IMsmtPeer.Send"/>.
        /// </summary>
        /// <param name="target">The remote peer to send to.</param>
        /// <param name="payload">
        /// The application message content. Not copied - the caller must not mutate it until the send
        /// completes, observable through the peer's events (see <see cref="IMsmtPeer.Send"/>).
        /// </param>
        /// <param name="options">Options governing how this payload is sent, or <see langword="null"/> to use the defaults.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
        public void Send(MsmtNameTarget target, ReadOnlyMemory<byte> payload, MsmtSendOptions? options = null) =>
            peer.Send(target, new NonOwningMemoryOwner(payload), options);

        /// <summary>
        /// Queues a message for delivery to <paramref name="target"/> and awaits the remote peer's
        /// acknowledgement, wrapping <paramref name="payload"/> in a non-pooled <see cref="IMemoryOwner{T}"/>
        /// so callers with an ordinary <see cref="ReadOnlyMemory{T}"/> don't need to manage one themselves to
        /// call <see cref="IMsmtPeer.Request"/>.
        /// </summary>
        /// <param name="target">The remote peer to send to.</param>
        /// <param name="payload">
        /// The application message content. Not copied - the caller must not mutate it until the request
        /// completes.
        /// </param>
        /// <param name="options">Options governing how this payload is sent, or <see langword="null"/> to use the defaults.</param>
        /// <param name="cancellation">Cancels the request before it completes.</param>
        /// <returns>The remote peer's acknowledgement.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
        public Task<MsmtResponse> Request(MsmtNameTarget target, ReadOnlyMemory<byte> payload, MsmtSendOptions? options = null, CancellationToken cancellation = default) =>
            peer.Request(target, new NonOwningMemoryOwner(payload), options, cancellation);
    }
}
