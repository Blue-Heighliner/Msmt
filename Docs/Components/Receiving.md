# Receiving

The incoming path: `MsmtServer` listens for inbound TLS connections and dispatches each one's messages
through `Received`, acknowledging once a subscriber decides via `IMsmtResponder`, or automatically once
every subscriber has run without deciding - unless one called `Defer()`, in which case it awaits that
responder's eventual decision, reading no further message on the connection meanwhile. Backs a peer's
receiver. `MsmtServerConnection` (`IMsmtLink`) represents one connection `MsmtServer` accepted, backing a
connection's `Receiver` link; unlike `MsmtClient`, it cannot send - messages back to that remote peer
always go out as a new client connection dialed to its own receiver.

## Listening

`MsmtPeer.StartListener` disposes any previous `MsmtServer`, creates a new one, and wires its plain C#
events into `MsmtPeer`'s own `MsmtEventSubject<T>` fields and into the connection registry:

- `Linking` attaches the newly accepted `MsmtServerConnection` to (or creates) the target's
  `MsmtConnection` via `AttachIncomingLink`/`AttachReceiver`, then republishes `Linking` - so a `Linking`
  subscriber already sees the connection registered. `MsmtServerConnection.IsConnected` (and so
  `IMsmtConnection.Receiver`) stays `false`/`null` until the handshake actually completes, mirroring
  `MsmtClient.IsConnected`'s symmetric meaning for the sending direction; `HandleConnection` calls
  `MarkConnected()` right before raising `Linked` once the handshake succeeds.
- `Linked` republishes, then checks whether this makes the connection newly `Connected` - identical to how
  the sender side's own `Linked` handler works, so `Connected` fires at the same point in the handshake
  lifecycle regardless of which direction just completed it.
- `LinkFailed`/`Unlinked` detach the link (`DetachIncomingLink`/`RemoveReceiver`) before republishing, and
  clean up a now-empty connection entry. A `LinkFailed` link never reached `Linked`, so it was never
  counted toward `Connected` and needs no corresponding `Disconnected` check.
- `Received` is republished as-is (`PackageChanged` is never actually raised by `MsmtServer`).

`Host` binds a `TcpListener` and starts a background `AcceptLoop` task. Each accepted `TcpClient` is handed
to its own `HandleConnection` task immediately, so one client's slow TLS handshake never delays accepting
the next; `HandleConnection` performs the TLS accept via `MsmtTlsServer`, raising `Linking` beforehand and
`Linked`/`LinkFailed` once the handshake resolves, then loops reading one message at a time (never more
than one in flight, per the ICD's request/acknowledge model):

1. Read the fixed 10-byte header. A malformed header gets an `InvalidPreambleOrModeUnsupported`
   acknowledgement and the connection closes.
2. If the `SessionModeNegotiation` flag is set, `RespondToSessionNegotiation` handles it (only honored as
   the very first message on the connection - see Connection lifecycle modes below) and the loop continues
   without touching `Received` at all.
3. If the `ReachabilityCheck` flag is set, the payload is echoed straight back with the same flag and the
   payload is never passed to `Received`.
4. Otherwise, `RespondToMessage` raises `Received` with a fresh `MsmtResponder`, waits for a decision (via
   `IsDeferred`/`WaitForDecision` if `Defer()` was called, or applies the automatic-accept default), and
   writes the acknowledgement. `IMsmtResponder.Accept`/`Reject` only work when an acknowledgement was
   actually requested; a plain `Send` always gets `MessageSuccess` set on its wire response, since there is
   no ack-based path through which the application could have rejected it.
5. If a `Session` connection's negotiated lifetime has elapsed, the loop returns and the connection closes;
   `Message`/`MessageWithRekeying` connections are instead left to the client side to close.

## Connection lifecycle modes

`RespondToSessionNegotiation` only honors a negotiation request as the very first message on a connection
(per the ICD); it agrees to the lesser of the client's proposed lifetime and
`MsmtHostOptions.MaximumSessionLifetime`, and the accepting `HandleConnection` loop closes the connection
once that agreed lifetime elapses.

## Idle eviction

`MsmtOptions.MaxIdleTime` has no practical effect in `Message` Mode, which always closes itself immediately
after each cycle; it can affect a `MessageWithRekeying` or `Session` connection sitting open between
messages, and never interrupts a connection with a message currently in progress.

`MsmtServerConnection` tracks `LastActivityUtc`/`IsIdle` (mirrored via `MarkIdle`/`MarkBusy`) across the gap
between message cycles. `HandleConnection` calls `MarkIdle` immediately before each "wait for the next
header" read and `MarkBusy` immediately after one arrives, so only that gap ever counts as idle. Because
BouncyCastle's TLS stream falls back to a blocking `byte[]`-based `ReadAsync` overload it never overrides
(see Disposal below), a cancelled token alone cannot interrupt that blocked read; `RunIdleTimeout` instead
loops a chain of clamped `Task.Delay` waits (`MsmtProtocol.ClampToMaxTimerDuration`) against the true
deadline and, only once genuinely elapsed, closes the underlying socket directly - mirroring shutdown's own
technique - forcing the blocked read to fail with an `IOException` that `HandleConnection` reports as a
`TimeoutException` (disambiguated from an ordinary disconnect via a shared single-element flag
`RunIdleTimeout` sets immediately before closing the socket). The loop is cancelled and awaited immediately
once the read settles - before touching the header - closing the same race window `MsmtClient` closes via
its queue.

## Disposal

`DisposeAsync` closes every accepted connection's raw socket directly (rather than relying on cancellation
alone) because BouncyCastle's TLS stream falls back to a blocking `byte[]` `ReadAsync` overload it never
overrides internally, which a cancelled token alone cannot interrupt.
