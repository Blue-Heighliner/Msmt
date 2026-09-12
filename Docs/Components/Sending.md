# Sending

The outgoing path: `MsmtClient` (`IMsmtLink`) sends messages to a single remote server over one TLS
connection, created and torn down per `MsmtOptions.Mode`, backing each connection's `Sender` link; and
`MsmtPackage` (`IMsmtPackage`), a single tagged payload queued on an `MsmtClient`, backing
`IMsmtPeer.Packages`/`GetPackage` and `MsmtPackageChangedEventArgs.Package`.

## Queueing and sending

`MsmtPeer.Send`/`.Request` resolve or create the target's `MsmtConnection` and call `EnsureSender` with a
factory (`CreateSender`) that builds a new `MsmtClient`, wires its events into `MsmtPeer`'s subjects,
attaches its cache-removal callback (`AttachToCache`, invoked by `Drop`/`Disconnect`/eviction to remove the
connection from the registry), and calls `Connect` - which starts `MsmtClient`'s two background loops
(`ProcessQueueLoop`, `KeepAliveLoop`).

`Send`/`Request` never touch the network directly - they call `Enqueue`, which wraps the payload and
options into a `PendingSend` record, records it in `activeSends` if tagged, adds it to a
`PriorityQueue<PendingSend, (int NegatedPriority, long Sequence)>` (highest priority first, FIFO within a
priority via the monotonically increasing `sendSequence`), raises `PackageChanged` as `Queued`, and signals
a `SemaphoreSlim`. A single background `ProcessQueueLoop` task dequeues and sends one item at a time, never
touching the caller's own thread, so a slow or blocked send never starves a higher-priority one waiting
behind it. For each item, `SendOverConnection`:

1. Calls `EnsureConnection`, which reuses the existing `RekeyableTlsClientProtocol` for
   `MessageWithRekeying`/still-valid `Session` connections, or closes any stale one and opens a fresh TLS
   connection otherwise - raising `Linking`/`Linked`/`LinkFailed` around the handshake, and negotiating a
   session lifetime immediately afterward for `Session` mode (see Connection lifecycle modes below).
2. Applies `MsmtSendOptions.Dscp` to the raw socket, if set, immediately before writing.
3. Writes the header and payload, raising `PackageChanged` as `Transmitting`, then (if an acknowledgement
   was requested) as `PendingAcknowledgement`.
4. Reads and validates the acknowledgement header and payload, completing the `Request`'s
   `TaskCompletionSource<MsmtResponse>` if one exists (or disposing the response payload if not, for a
   plain `Send`).
5. Applies the mode-specific post-send action (see below), then raises `PackageChanged` as `Completed` or
   `Cancelled`.

Any exception or cancellation encountered mid-frame closes the connection (`CloseConnection`) to avoid
desynchronizing a later send on the same client. Since BouncyCastle's TLS stream doesn't honor a
cancellation token on a write or read already blocked, `SendOverConnection` registers a callback on its
linked token (covering this item's own `CancellationToken`, `Cancel(tag)`'s `CancelSource`, and this
client's own disposal) that force-closes the raw socket the instant any of the three fires
(`ForceCloseSocket`) - the only way any of them actually interrupts a send in flight rather than leaving it
blocked until the remote peer eventually responds. The resulting `IOException` is reported to the caller as
an `OperationCanceledException` whenever the linked token was in fact cancelled, rather than a confusing
"connection closed" error.

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
  which point the next send transparently reconnects. A background `KeepAliveLoop` task (ticking every 15
  seconds, checking against a randomized 3-5 minute idle threshold recomputed per connection) sends an
  empty `ReachabilityCheck` payload whenever a `Session` connection has gone that long without any
  application traffic, so it isn't dropped for inactivity between application sends.

## Tracking packages

`MsmtClient` tracks a tagged send's status in two structures: `activeSends` (the still-outstanding
`PendingSend`, removed once finished, backing `Cancel`) and `sendStatuses` (every tag's last known status,
including finished ones, backing `GetStatus`/`GetPackage`). To bound `sendStatuses`' otherwise-unbounded
growth for a long-lived client given a uniquely tagged send per message, a completed or cancelled tag is
queued into `completedSendTags`, and the oldest is evicted from `sendStatuses` once more than 10,000
(`maxTrackedCompletedSendStatuses`) have accumulated - so `GetStatus`/`GetPackage` keeps working for every
in-flight or recently finished send and only forgets a tag once far enough behind more recent completions.
`MsmtPackage` wraps a `(client, tag, status)` triple; its `Status` getter is lazy - until it has observed a
final status (`Completed`/`Cancelled`) it re-reads `client.GetStatus(tag)` on every access, then latches on
a final one rather than continuing to re-read it. `MsmtPackage` is a thin, on-demand view over the client's
own tracking state, not a copy of it.

## Idle eviction

`MsmtOptions.MaxIdleTime` has no practical effect in `Message` Mode, which always closes itself immediately
after each cycle before the rule ever gets a chance to apply; it can affect a `MessageWithRekeying` or
`Session` connection sitting open between messages. It is enforced per-connection rather than by a shared
polling loop, so detection is prompt rather than bounded by a fixed sweep interval, and never interrupts a
connection with a send currently queued or in progress.

A single `CancellationTokenSource`/`Task.Delay` timer throws `ArgumentOutOfRangeException` beyond roughly
49.7 days (`MsmtProtocol.MaxTimerDuration`, `uint.MaxValue - 1` milliseconds), so `MsmtClient` never waits
out a configured `MaxIdleTime` in one timer - it chains as many clamped-length waits as needed
(`MsmtProtocol.ClampToMaxTimerDuration`) to honor an arbitrarily long value exactly, rather than crashing or
firing early.

`MsmtClient` tracks `LastActivityUtc` (when its last send completed) and `IsIdle` (`outstandingSends == 0` -
no send currently queued or in flight). After each completed send that leaves the connection open
(`MessageWithRekeying`/`Session`), `ScheduleIdleCheck` starts a one-shot, clamped `Task.Delay` that, once
elapsed, enqueues an idle-check placeholder item (`PendingSend.IsIdleCheck`). Routing the check through the
same single-consumer queue as real sends - rather than closing the connection directly from the timer -
guarantees it can never race a concurrently processing send: by the time `HandleIdleCheck` runs, nothing
else is mid-cycle. If the clamped wait was shorter than the configured `MaxIdleTime`, or a send refreshed
`LastActivityUtc` while the check was already in flight, `HandleIdleCheck` finds the connection not yet
genuinely idle and calls `ScheduleIdleCheck` again for whatever time remains, so it only ever closes a
connection once truly idle for the full configured duration. `idleCheckScheduled` debounces this across a
burst of sends - a second call while one is already pending is a no-op, since the pending one will observe
the latest activity and reschedule itself if needed.

## Disposal

`DisposeAsync` calls `ForceCloseSocket` immediately after cancelling `disposalCancellation` and *before*
awaiting `processingLoop`, since awaiting first would otherwise hang forever were the loop currently
blocked inside `SendOverConnection`'s own write or acknowledgement read - only `ForceCloseSocket` can
unblock that, not the cancelled token alone. `ProcessQueueLoop` remains the sole owner of the `connection`
field and its `Unlinked` bookkeeping either way: `ForceCloseSocket` only touches the raw socket, letting
`SendOverConnection`'s own exception handling perform the actual `CloseConnection` once the resulting
exception unwinds on its own thread.
