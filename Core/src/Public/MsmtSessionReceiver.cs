namespace BlueHeighliner.Msmt;

/// <summary>Handles a single application message received over one of an <see cref="IMsmtSessionPeer"/>'s connections.</summary>
/// <param name="connection">The connection the message was received on.</param>
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
public delegate void MsmtSessionReceiver(IMsmtConnection connection, IMemoryOwner<byte> payload, IMsmtResponder? responder);
