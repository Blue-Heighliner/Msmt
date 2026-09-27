namespace BlueHeighliner.Msmt;

/// <summary>
/// A peer-to-peer MSMT API for message mode and message-with-rekeying mode: it sends messages directly to
/// targets and receives messages directly from them, and the TLS connections behind that are managed
/// internally and never exposed. A peer listens for incoming connections and opens an outgoing one per
/// remote target sent to, created on demand and disconnected in the background once unused for too long or
/// once too many are open. Connections here are one-directional; use <see cref="IMsmtSessionPeer"/> for
/// bidirectional session connections. See <see cref="MsmtMessagePeerOptions"/> for the timeouts involved.
/// </summary>
public interface IMsmtMessagePeer : IMsmtPeer
{
    /// <summary>
    /// Gets or sets the <see cref="MsmtMessageReceiver"/> invoked for every application message this peer's
    /// listener accepts; <see langword="null"/> (the default) accepts every message automatically without
    /// reporting it anywhere. Different connections may invoke this concurrently, so it must be safe to run
    /// at once for more than one message.
    /// </summary>
    MsmtMessageReceiver? Receiver { get; set; }

    /// <summary>
    /// Queues a message for delivery to <paramref name="target"/>, creating and caching a new outgoing
    /// connection to it if one isn't already open, without requesting or waiting for a response. This
    /// method only enqueues the payload; it does not wait for the send to complete. Observe a queued send's
    /// progress through <see cref="IMsmtPeer.PackageChanged"/>. Use <see cref="Request"/> instead to await
    /// the remote peer's acknowledgement.
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
    /// <exception cref="ObjectDisposedException">This peer has been disposed.</exception>
    void Send(MsmtNameTarget target, IMemoryOwner<byte> payload, MsmtSendOptions? options = null);

    /// <summary>
    /// Queues a message for delivery to <paramref name="target"/>, creating and caching a new outgoing
    /// connection to it if one isn't already open, and asynchronously awaits the remote peer's
    /// acknowledgement. Observe a queued request's progress through <see cref="IMsmtPeer.PackageChanged"/>.
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
    /// <exception cref="ObjectDisposedException">This peer has been disposed.</exception>
    Task<MsmtResponse> Request(MsmtNameTarget target, IMemoryOwner<byte> payload, MsmtSendOptions? options = null, CancellationToken cancellation = default);

    /// <summary>
    /// Creates <see cref="IMsmtMessagePeer"/> instances. Registered for dependency injection by <see
    /// cref="MsmtServiceCollectionExtensions.AddMsmt"/>; construct <see cref="Factory"/> directly, with its
    /// parameterless constructor, when not using an IoC container.
    /// </summary>
    public interface IFactory
    {
        /// <summary>Creates a peer configured with the given options.</summary>
        /// <param name="options">The peer's shared credentials and connection-behavior defaults.</param>
        /// <returns>The new peer.</returns>
        /// <exception cref="ArgumentOutOfRangeException">A timeout in <paramref name="options"/> is not positive.</exception>
        IMsmtMessagePeer Create(MsmtMessagePeerOptions options);
    }

    /// <inheritdoc cref="IFactory" />
    public sealed class Factory : IFactory
    {
        /// <inheritdoc />
        public IMsmtMessagePeer Create(MsmtMessagePeerOptions options) => new MsmtMessagePeer(options);
    }
}

/// <summary>
/// Extension members for <see cref="IMsmtMessagePeer"/>.
/// </summary>
public static class MsmtMessagePeerExtensions
{
    extension(IMsmtMessagePeer peer)
    {
        /// <summary>
        /// Queues a message for delivery to <paramref name="target"/>, wrapping <paramref name="payload"/>
        /// in a non-pooled <see cref="IMemoryOwner{T}"/> so callers with an ordinary <see
        /// cref="ReadOnlyMemory{T}"/> don't need to manage one themselves to call <see
        /// cref="IMsmtMessagePeer.Send"/>.
        /// </summary>
        /// <param name="target">The remote peer to send to.</param>
        /// <param name="payload">
        /// The application message content. Not copied - the caller must not mutate it until the send
        /// completes (see <see cref="IMsmtMessagePeer.Send"/>).
        /// </param>
        /// <param name="options">Options governing how this payload is sent, or <see langword="null"/> to use the defaults.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
        /// <exception cref="ObjectDisposedException">The peer has been disposed.</exception>
        public void Send(MsmtNameTarget target, ReadOnlyMemory<byte> payload, MsmtSendOptions? options = null) =>
            peer.Send(target, new NonOwningMemoryOwner(payload), options);

        /// <summary>
        /// Queues a message for delivery to <paramref name="target"/> and awaits the remote peer's
        /// acknowledgement, wrapping <paramref name="payload"/> in a non-pooled <see cref="IMemoryOwner{T}"/>
        /// so callers with an ordinary <see cref="ReadOnlyMemory{T}"/> don't need to manage one themselves to
        /// call <see cref="IMsmtMessagePeer.Request"/>.
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
        /// <exception cref="ObjectDisposedException">The peer has been disposed.</exception>
        public Task<MsmtResponse> Request(MsmtNameTarget target, ReadOnlyMemory<byte> payload, MsmtSendOptions? options = null, CancellationToken cancellation = default) =>
            peer.Request(target, new NonOwningMemoryOwner(payload), options, cancellation);
    }
}
