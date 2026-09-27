namespace BlueHeighliner.Msmt;

/// <summary>
/// A peer-to-peer session mode API: listens for incoming session connections and opens outgoing ones,
/// tracking all of them together. Every connection is bidirectional: either side may send over it, and
/// the other acknowledges. Unlike <see cref="IMsmtMessagePeer"/>, connections here are real objects the
/// caller gets back and is responsible for managing - this peer never disconnects one on its own beyond a
/// handshake timeout, a stall, or the negotiated session lifetime ending; see <see
/// cref="MsmtSessionPeerOptions"/> for the timeouts involved.
/// </summary>
public interface IMsmtSessionPeer : IMsmtPeer
{
    /// <summary>
    /// Gets an observable that publishes an incoming connection once it is established. Connections this
    /// peer opens itself are not raised here, since <see cref="Connect"/> hands them straight back.
    /// </summary>
    IObservable<IMsmtConnection> Connected { get; }

    /// <summary>
    /// Gets an observable that publishes whenever one of this peer's connections, incoming or outgoing,
    /// ends - including one that never finished connecting, such as a client that never completes its
    /// handshake or does not negotiate a session, or a remote peer that never answers one this peer opened.
    /// </summary>
    IObservable<MsmtDisconnection> Disconnected { get; }

    /// <summary>
    /// Gets or sets the <see cref="MsmtSessionReceiver"/> invoked whenever any of this peer's connections
    /// receives a message from the remote peer; <see langword="null"/> (the default) accepts every message
    /// automatically without reporting it anywhere. Different connections may invoke this concurrently, so
    /// it must be safe to run at once for more than one message.
    /// </summary>
    MsmtSessionReceiver? Receiver { get; set; }

    /// <summary>Gets a snapshot of this peer's currently open connections, both accepted and opened, including ones still <see cref="MsmtConnectionStatus.Connecting"/>.</summary>
    IReadOnlyList<IMsmtConnection> Connections { get; }

    /// <summary>
    /// Starts opening a connection to <paramref name="target"/> and returns it immediately, still <see
    /// cref="MsmtConnectionStatus.Connecting"/>: the TCP connect, TLS handshake, and session negotiation all
    /// happen in the background. Await <see cref="IMsmtConnection.Wait"/> to find out whether it succeeded,
    /// or dispose the returned connection to abandon the attempt.
    /// </summary>
    /// <param name="target">The remote peer to connect to.</param>
    /// <returns>The new connection, still connecting.</returns>
    /// <exception cref="ObjectDisposedException">This peer has been disposed.</exception>
    IMsmtConnection Connect(MsmtNameTarget target);

    /// <summary>Creates <see cref="IMsmtSessionPeer"/> instances, so code that depends on one can be tested or configured without constructing it directly.</summary>
    public interface IFactory
    {
        /// <summary>Creates a session peer with no connections. Call <see cref="IMsmtPeer.StartListener"/> separately to also start listening.</summary>
        /// <param name="options">The peer's credentials and connection-behavior settings.</param>
        /// <returns>A new session peer with no connections, not yet listening.</returns>
        /// <exception cref="ArgumentOutOfRangeException">A timeout or interval in <paramref name="options"/> is not positive, or the keep-alive minimum exceeds its maximum.</exception>
        IMsmtSessionPeer Create(MsmtSessionPeerOptions options);
    }

    /// <inheritdoc cref="IFactory" />
    public sealed class Factory : IFactory
    {
        /// <inheritdoc />
        public IMsmtSessionPeer Create(MsmtSessionPeerOptions options) => new MsmtSessionPeer(options);
    }
}
