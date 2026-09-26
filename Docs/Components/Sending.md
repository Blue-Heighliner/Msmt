# Sending

The outgoing path: `MsmtClient` (`IMsmtLink`) sends messages to a single remote server over one TLS
connection, created and torn down per `MsmtOptions.Mode`, backing each connection's `Sender` link; and
`MsmtPackage` (`IMsmtPackage`), a single tagged payload queued on an `MsmtClient`, backing
`IMsmtPeer.Packages`/`GetPackage` and `MsmtPackageChangedEventArgs.Package`.

## Queueing and sending

`MsmtPeer.Send`/`.Request` resolve or create the target's `MsmtConnection` and call `ReserveSender` with a
factory (`CreateSender`) that builds a new `MsmtClient`, wires its events into `MsmtPeer`'s subjects,
attaches its cache-removal callback (`AttachToCache`, invoked by `Drop`/`Disconnect` to remove the
connection from the registry), and calls `Connect` - which starts `MsmtClient`'s two background loops
(`ProcessQueueLoop`, `MaintenanceLoop`). The returned client stays reserved until the send is queued on it.

`Send`/`Request` never touch the network directly - they call `Enqueue`, which refuses once the client is
disposed (`Send`/`Request` then throw `ObjectDisposedException`), and otherwise wraps the payload and
options into a `PendingSend` record, registers it with the peer's package tracker if tagged, adds it to a
`PriorityQueue<PendingSend, (int NegatedPriority, long Sequence)>` (highest priority first, FIFO within a
priority via the monotonically increasing `sendSequence`), raises `PackageChanged` as `Queued`, and signals
a `SemaphoreSlim`. A single background `ProcessQueueLoop` task dequeues and sends one item at a time, never
touching the caller's own thread, so a slow or blocked send never starves a higher-priority one waiting
behind it. For each item, `SendOverConnection`:

1. Calls `EnsureConnection`, which reuses the existing `RekeyableTlsClientProtocol` for
   `MessageWithRekeying`/still-valid `Session` connections, or closes any stale one and opens a fresh TLS
   connection otherwise - raising `Linking`, then `Linked` or (for any failure, cancellation included)
   `LinkFailed` around the handshake, and negotiating a session lifetime immediately afterward for
   `Session` mode (see Connection lifecycle modes below). `OpenConnection` bounds the TCP connect and
   handshake with `HandshakeTimeout` and enables TCP keep-alive; a reused connection is first checked with
   `IsSocketAlive` and replaced if the peer already closed it.
2. Applies `MsmtSendOptions.Dscp` to the raw socket, if set, immediately before writing.
3. Writes the header and payload under `StallTimeout`, raising `PackageChanged` as `Transmitting`, then (if
   an acknowledgement was requested) as `PendingAcknowledgement`.
4. Reads and validates the acknowledgement header, under `ResponseTimeout`, and its payload, under
   `StallTimeout`, completing the `Request`'s `TaskCompletionSource<MsmtResponse>` if one exists (or
   disposing the response payload if not, for a plain `Send`). `Exchange` performs steps 3 and 4 for sends
   and session negotiation alike.
5. Applies the mode-specific post-send action (see below), then raises `PackageChanged` as `Completed` or
   `Cancelled`.

Any exception or cancellation encountered mid-frame closes the connection (`CloseConnection`) to avoid
desynchronizing a later send on the same client. Since neither BouncyCastle's blocking handshake nor its
TLS stream honors a cancellation token once blocked, `SendOverConnection` registers a callback on its
linked token (covering this item's own `CancellationToken`, `Cancel(tag)`'s `CancelSource`, and this
client's own disposal) that force-closes the raw socket the instant any of the three fires
(`ForceCloseSocket`) - the only way any of them actually interrupts a handshake or send in flight rather
than leaving it blocked until the remote peer eventually responds. The resulting `IOException` is reported
to the caller as an `OperationCanceledException` whenever the linked token was in fact cancelled, rather
than a confusing "connection closed" error.

## Connection lifecycle modes

Implemented entirely inside `SendOverConnection`/`EnsureConnection`:

- `Message` (default) - `EnsureConnection` always opens a fresh `RekeyableTlsClientProtocol`;
  `SendOverConnection` closes it again immediately after the acknowledgement is read.
- `MessageWithRekeying` - the same connection is reused across sends. After each acknowledgement,
  `SendOverConnection` calls `protocol.Rekey()` (a TLS 1.3 `KeyUpdate`) unless `messagesSinceConnect` has
  reached `MsmtOptions.RekeyLimit`, in which case the connection is closed and the next send opens a new
  one.
- `Session` - `EnsureConnection` negotiates a lifetime via `NegotiateSession` immediately after
  connecting (a `SessionModeNegotiation`-flagged message carrying the proposed lifetime in seconds as its
  payload) and reuses the connection for every subsequent send until `sessionExpiresAtUtc` passes, at
  which point the next send transparently reconnects; the maintenance check also closes the connection at
  expiry without waiting for a send. It sends an empty `ReachabilityCheck` payload whenever a `Session`
  connection has gone a randomized interval, chosen per connection, without any message, so it isn't
  dropped for inactivity between application sends.

## Tracking packages

`MsmtPeer` owns one `MsmtPackageTracker` and hands it to every `MsmtClient` it creates, so a tagged send
is tracked by the peer rather than by whichever pooled client carried it. `GetPackage`, `Packages`, and
`Cancel` keep working after that client has been idle-evicted and disposed. The tracker keeps an entry per
tag (target, last known status, and the send's cancellation source while it is outstanding), backing
`Cancel`, `GetStatus`, and `GetPackage` alike. To bound its growth for a long-lived peer given a uniquely
tagged send per message, a completed or cancelled tag is queued with its finish time and forgotten five
minutes later, with no cap on how many are tracked. Expiry runs from the peer's eviction loop and lazily
from every tracker member, so an expired tag is never returned, and removes by instance so a newer send
that reused the tag is never forgotten. `MsmtPackage` wraps a `(tracker, tag, target, status)` tuple; its `Status` getter is
lazy - until it has observed a final status (`Completed`/`Cancelled`) it re-reads the tracker on every
access, then latches on a final one. Tags identify a send peer-wide, so reusing one replaces the earlier
send's tracking.

## Activity and eviction

`MsmtClient` tracks `LastActivityUtc` (when its last application send completed; keep-alives and session
negotiation do not count), `LastWireActivityUtc` (when any message completed, which is what schedules
keep-alives), and `IsIdle` (`outstandingSends == 0`, so no send queued, in flight, or reserved; maintenance
items are not counted). It enforces no idle limit itself: `MsmtPeer`'s eviction loop compares
`LastActivityUtc` with `MaxIdleTime`, so the limit needs no per-client timers and an arbitrarily long
value, up to `TimeSpan.MaxValue`, needs no clamping.

## Disposal

`DisposeAsync` calls `ForceCloseSocket` immediately after cancelling `disposalCancellation` and *before*
awaiting `processingLoop`, since awaiting first would otherwise hang forever were the loop currently
blocked inside `SendOverConnection`'s own write or acknowledgement read - only `ForceCloseSocket` can
unblock that, not the cancelled token alone. `ProcessQueueLoop` remains the sole owner of the `connection`
field and its `Unlinked` bookkeeping either way: `ForceCloseSocket` only touches the raw socket, letting
`SendOverConnection`'s own exception handling perform the actual `CloseConnection` once the resulting
exception unwinds on its own thread. `CloseConnection` swaps the connection out atomically, so a
synchronous `Dispose` racing that loop still raises `Unlinked` exactly once.

Disposal is idempotent and marks the client disposed under the same lock `Enqueue` checks, so every send
is either queued before that point (and drained, cancelled, by shutdown) or refused - never stranded in a
queue nothing will process. `disposalCancellation` and `queueSignal` are deliberately never disposed:
background work may still touch them after shutdown, and neither holds anything that needs releasing.
