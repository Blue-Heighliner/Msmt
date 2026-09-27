namespace BlueHeighliner.Msmt;

/// <summary>Handles a single application message an <see cref="IMsmtMessagePeer"/>'s listener accepted, sent to it via <see cref="IMsmtMessagePeer.Send"/> or <see cref="IMsmtMessagePeer.Request"/> by another peer.</summary>
/// <param name="source">The address and port the message came from, as observed by this side.</param>
/// <param name="identity">The identity the remote peer presented and this side verified during the TLS handshake.</param>
/// <param name="payload">
/// The message payload, exactly as sent. Backed by a pooled buffer the peer still owns and disposes once
/// the returned <see cref="ValueTask{MsmtReceiveResult}"/> completes, however it completes; a handler that
/// needs to keep the payload longer must copy it before returning.
/// </param>
/// <param name="isResponseRequested">Whether the sender requested an acknowledgement for this message; if <see langword="false"/>, this handler must return <see langword="null"/>.</param>
/// <returns>
/// How to acknowledge this message, once this handler is done with it, if <paramref
/// name="isResponseRequested"/> is <see langword="true"/> - returning <see langword="null"/> then is an
/// error. If <paramref name="isResponseRequested"/> is <see langword="false"/>, this must return <see
/// langword="null"/>, which sends no acknowledgement at all - not even a wire-level one; returning a
/// non-null result then is also an error, closing the connection with an <see
/// cref="InvalidOperationException"/>.
/// </returns>
public delegate ValueTask<MsmtReceiveResult?> MsmtMessageReceiver(MsmtTarget source, MsmtIdentity identity, ReadOnlyMemory<byte> payload, bool isResponseRequested);
