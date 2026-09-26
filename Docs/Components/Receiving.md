# Receiving

The incoming path: `MsmtServer` listens for inbound TLS connections and dispatches each one's messages
through `Received`, acknowledging once a subscriber decides via `IMsmtResponder`, or automatically once
every subscriber has run without deciding - unless one called `Defer()`, in which case it awaits that
responder's eventual decision, reading no further message on the connection meanwhile. Backs a peer's
receiver. `MsmtServerConnection` (`IMsmtLink`) represents one connection `MsmtServer` accepted, backing a
connection's `Receiver` link; unlike `MsmtClient`, it cannot send - messages back to that remote peer
always go out as a new client connection dialed to its own receiver.

## Listening

`MsmtPeer.StartListener` stops any previous listener, creates a new `MsmtServer` (disposing it again, and
leaving the peer not listening, if binding fails), and wires its plain C# events into `MsmtPeer`'s own
`MsmtEventSubject<T>` fields and into the connection registry:

- `Linking` attaches the newly accepted `MsmtServerConnection` to (or creates) the target's
  `MsmtConnection` via `AttachIncomingLink`/`TryAttachReceiver`, then republishes `Linking` - so a `Linking`
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
to its own `HandleConnection` task via `Task.Run`, since BouncyCastle's handshake blocks synchronously, so
one client's slow or stalled TLS handshake never delays accepting the next. A failed accept that isn't
caused by shutdown is retried after a short delay rather than ending the loop. `HandleConnection` registers
the socket for shutdown before the handshake (so disposal also aborts a stalled one), performs the TLS
accept via `MsmtTlsServer`, raising `Linking` beforehand and `Linked`/`LinkFailed` once the handshake
resolves, then hands off to `ServeMessages`, which loops reading one message at a time (never more than
one in flight, per the ICD's request/acknowledge model):

1. Read the fixed 10-byte header: its first byte with no stall timeout, since the wait for a next message
   is legitimate idleness (except right after the handshake, where `HandshakeTimeout` applies), then the
   rest of it and the payload under `StallTimeout`. A malformed header gets an
   `InvalidPreambleOrModeUnsupported` acknowledgement and the connection closes.
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
5. If a `Session` connection's negotiated lifetime has elapsed, the loop returns and the connection closes.
   Any other connection is closed after `MsmtHostOptions.RekeyLimit` messages, as its client closes it too.

## Connection lifecycle modes

`RespondToSessionNegotiation` only honors a negotiation request as the very first message on a connection
(per the ICD); it agrees to the lesser of the client's proposed lifetime and
`MsmtHostOptions.MaximumSessionLifetime`, and `ServeMessages` closes the connection once that agreed
lifetime elapses. Only an accepted negotiation sets the expiry, so a rejected later attempt can't clear it.
A silent client is cut off by `CloseAtSessionExpiry`, a timer started at negotiation that closes the socket
at expiry plus a short grace period, if the connection is idle at that moment; a chain of clamped
`Task.Delay` waits keeps an arbitrarily long lifetime exact.

## Activity and eviction

`MsmtServerConnection` tracks `IsIdle` (mirrored via `MarkIdle`/`MarkBusy`) and `LastActivityUtc`.
`ServeMessages` calls `MarkIdle` before each wait for a next header and `MarkBusy` once a byte arrives, so
only that gap counts as idle. `MarkActivity` runs after an application message has been acknowledged;
reachability checks and session negotiation do not count, so a client that only sends keep-alives still
ages. `MsmtPeer`'s eviction loop compares `LastActivityUtc` with `MaxIdleTime` and `MaxConnectionCount`, and
calls `Evict`, which records a `TimeoutException` as the reason before closing the socket. `HandleConnection`
reports that reason, or `null` for a deliberate normal close such as a session ending, instead of the read
failure the close causes; a stall caught by the watchdog is reported as a `TimeoutException` too.

## Disposal

`DisposeAsync` closes every accepted connection's raw socket directly (rather than relying on cancellation
alone) because BouncyCastle's handshake blocks synchronously and its TLS stream falls back to a blocking
`byte[]` `ReadAsync` overload it never overrides internally, neither of which a cancelled token can
interrupt. Whatever ends a connection's loop - including an exception thrown by a `Received` subscriber,
or shutdown while a responder is still deferred - is reported through `Unlinked` rather than rethrown, so
one connection never makes disposal fail. `Dispose`/`DisposeAsync` are idempotent.
