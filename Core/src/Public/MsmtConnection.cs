namespace BlueHeighliner.Msmt;

/// <summary>
/// A session connection with one remote peer, opened or accepted by an <see cref="IMsmtSessionPeer"/>.
/// Either side may send messages over it, and the other side acknowledges them; at most one message per
/// direction is in flight at a time. It carries no events of its own - every notification about it,
/// including messages it receives, is raised through the <see cref="IMsmtSessionPeer"/> that owns it.
/// </summary>
public interface IMsmtConnection : IDisposable, IAsyncDisposable
{
    /// <summary>Gets this connection's current lifecycle stage.</summary>
    MsmtConnectionStatus Status { get; }

    /// <summary>Gets which side opened this connection.</summary>
    MsmtConnectionDirection Direction { get; }

    /// <summary>Gets the remote peer's address and port.</summary>
    MsmtTarget Remote { get; }

    /// <summary>Gets the identity the remote peer presented and this side verified during the TLS handshake, or <see langword="null"/> until <see cref="Status"/> reaches <see cref="MsmtConnectionStatus.Connected"/>.</summary>
    MsmtIdentity? Identity { get; }

    /// <summary>Gets when this connection's negotiated lifetime ends and it will be closed, or <see langword="null"/> until <see cref="Status"/> reaches <see cref="MsmtConnectionStatus.Connected"/>.</summary>
    DateTime? Expiration { get; }

    /// <summary>
    /// Waits for <see cref="Status"/> to leave <see cref="MsmtConnectionStatus.Connecting"/>, which happens
    /// almost immediately if it has already done so.
    /// </summary>
    /// <param name="cancellation">Cancels the wait without affecting the connection itself.</param>
    /// <returns><see langword="true"/> if it reached <see cref="MsmtConnectionStatus.Connected"/>; <see langword="false"/> if it went straight to <see cref="MsmtConnectionStatus.Disconnected"/> without ever connecting.</returns>
    Task<bool> Wait(CancellationToken cancellation = default);

    /// <summary>
    /// Queues a message for delivery to the remote peer, without requesting or waiting for a response. This
    /// method only enqueues the payload; it does not wait for the send to complete, and may be called while
    /// still <see cref="MsmtConnectionStatus.Connecting"/> - it is simply held until then. Observe a queued
    /// send's progress through the owning <see cref="IMsmtPeer.PackageChanged"/>. Use <see
    /// cref="Request"/> instead to await the acknowledgement.
    /// </summary>
    /// <param name="payload">
    /// The application message content, rented from a pool. If this method returns without throwing,
    /// ownership of <paramref name="payload"/> transfers to this connection, which disposes it once the send
    /// finishes, however it ends; if it throws, the caller retains ownership.
    /// </param>
    /// <param name="options">Options governing how this payload is sent, or <see langword="null"/> to use the defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
    /// <exception cref="ObjectDisposedException">This connection has disconnected.</exception>
    void Send(IMemoryOwner<byte> payload, MsmtSendOptions? options = null);

    /// <summary>
    /// Queues a message for delivery to the remote peer and asynchronously awaits its acknowledgement. May
    /// be called while still <see cref="MsmtConnectionStatus.Connecting"/> - it is simply held until then.
    /// </summary>
    /// <param name="payload">
    /// The application message content, rented from a pool. If this method returns without throwing,
    /// ownership of <paramref name="payload"/> transfers to this connection, which disposes it once the
    /// request finishes, however it ends; if it throws, the caller retains ownership.
    /// </param>
    /// <param name="options">Options governing how this payload is sent, or <see langword="null"/> to use the defaults.</param>
    /// <param name="cancellation">Cancels the request before it completes. Cancelling one already written closes the connection, since its acknowledgement can no longer be matched.</param>
    /// <returns>The remote peer's acknowledgement.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
    /// <exception cref="ObjectDisposedException">This connection has disconnected.</exception>
    /// <exception cref="TimeoutException">The remote peer did not acknowledge within <see cref="MsmtOptions.ResponseTimeout"/>, or stalled mid-transfer.</exception>
    Task<MsmtResponse> Request(IMemoryOwner<byte> payload, MsmtSendOptions? options = null, CancellationToken cancellation = default);
}

/// <summary>
/// Extension members for <see cref="IMsmtConnection"/>.
/// </summary>
public static class MsmtConnectionExtensions
{
    extension(IMsmtConnection connection)
    {
        /// <summary>
        /// Queues a message for delivery, wrapping <paramref name="payload"/> in a non-pooled <see
        /// cref="IMemoryOwner{T}"/> so callers with an ordinary <see cref="ReadOnlyMemory{T}"/> don't need to
        /// manage one themselves to call <see cref="IMsmtConnection.Send"/>.
        /// </summary>
        /// <param name="payload">The application message content. Not copied - the caller must not mutate it until the send completes.</param>
        /// <param name="options">Options governing how this payload is sent, or <see langword="null"/> to use the defaults.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
        /// <exception cref="ObjectDisposedException">The connection has disconnected.</exception>
        public void Send(ReadOnlyMemory<byte> payload, MsmtSendOptions? options = null) =>
            connection.Send(new NonOwningMemoryOwner(payload), options);

        /// <summary>
        /// Queues a message for delivery and awaits its acknowledgement, wrapping <paramref name="payload"/> in
        /// a non-pooled <see cref="IMemoryOwner{T}"/> so callers with an ordinary <see
        /// cref="ReadOnlyMemory{T}"/> don't need to manage one themselves to call <see
        /// cref="IMsmtConnection.Request"/>.
        /// </summary>
        /// <param name="payload">The application message content. Not copied - the caller must not mutate it until the request completes.</param>
        /// <param name="options">Options governing how this payload is sent, or <see langword="null"/> to use the defaults.</param>
        /// <param name="cancellation">Cancels the request before it completes.</param>
        /// <returns>The remote peer's acknowledgement.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
        /// <exception cref="ObjectDisposedException">The connection has disconnected.</exception>
        public Task<MsmtResponse> Request(ReadOnlyMemory<byte> payload, MsmtSendOptions? options = null, CancellationToken cancellation = default) =>
            connection.Request(new NonOwningMemoryOwner(payload), options, cancellation);
    }
}
