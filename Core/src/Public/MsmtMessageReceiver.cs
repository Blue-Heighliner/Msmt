namespace BlueHeighliner.Msmt;

/// <summary>Handles a single application message an <see cref="IMsmtMessagePeer"/>'s listener accepted, sent to it via <see cref="IMsmtMessagePeer.Send"/> or <see cref="IMsmtMessagePeer.Request"/> by another peer.</summary>
/// <param name="source">The address and port the message came from, as observed by this side.</param>
/// <param name="identity">The identity the remote peer presented and this side verified during the TLS handshake.</param>
/// <param name="payload">
/// The message payload, exactly as sent, rented from a pool. Ownership transfers to this handler, which
/// must dispose it, as soon as it is finished with it.
/// </param>
/// <param name="responder">
/// How to acknowledge the message, or <see langword="null"/> if the sender did not request an
/// acknowledgement, in which case none is sent at all, not even a wire-level one. When not <see
/// langword="null"/>, the application must answer it, exactly once, but not necessarily before this handler
/// returns: this handler completing says nothing about whether it was answered, and one that is never
/// answered leaves the sender waiting until its <see cref="MsmtOptions.ResponseTimeout"/>.
/// </param>
public delegate void MsmtMessageReceiver(MsmtTarget source, MsmtIdentity identity, IMemoryOwner<byte> payload, IMsmtResponder? responder);
