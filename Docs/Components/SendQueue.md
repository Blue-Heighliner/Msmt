# Send queue and package tracking

`MsmtOutbox` queues sends and runs them one at a time; `MsmtPackageTracker` and `MsmtPackage` track the
tagged ones. They are what `Send`/`Request` do underneath, for a session connection and for a peer's
per-target sender alike.

## Queueing and sending

`Send`/`Request` never touch the network directly. They build an `MsmtPendingSend` (payload, flags, tag,
DSCP, the caller's cancellation token and, for a `Request`, the source that completes with the
acknowledgement) and enqueue it into a `PriorityQueue` (highest priority first, first-in-first-out within a
priority via a rising sequence number), raise `PackageChanged` as `Queued`, and signal a `SemaphoreSlim`. A
single background loop dequeues and processes one item at a time, so a slow send never starves a
higher-priority one waiting behind it, and only one message is ever in flight.

What sending means is a delegate supplied by the owner. A session connection sends over itself. A peer's
per-target sender opens whatever connection its mode needs and then does the same. The delegate reports
`Transmitting` and `PendingAcknowledgement` as it goes and returns the acknowledgement, or `null` for a
plain send that never requested one and so was never written back; the outbox uses a non-null one to
complete the request, or disposes it if the send had none of its own to complete - `Send`'s own requests
never carry one either way, so a `null` acknowledgement is simply the ordinary case for those. The final status is `Completed`,
`Cancelled` when the caller's token or `IMsmtPackage.Cancel` fired, or `Failed` with the exception for
anything else, including a connection closing under the send. Disposing the outbox cancels the queue
without waiting for the send in flight, but that is not counted as the caller cancelling it.

Ownership follows the API: if `Enqueue` returns, the outbox owns the payload and disposes it once the send
ends however it ends; if the outbox is disposed, `Enqueue` refuses and the caller keeps it.

## Idle and activity

`IsIdle` is true while nothing is queued, in flight, or reserved. A keep-alive is queued like any send but
never counts, so it can never keep an outbox from being idle. `LastActivityUtc` moves only when an
application send completes, not for a keep-alive, so unused connections still age while being kept alive on
the wire. `Reserve` holds an outbox non-idle from when a caller finds it until it has queued on it, so
eviction cannot dispose it in between.

## Tracking packages

`MsmtPackageTracker` keeps an entry per tag: the target, the last known status, and the send's cancellation
source while it is outstanding. It backs `Cancel`, `GetStatus`, `GetPackage` and `Packages` - declared on
the shared `IMsmtPeer`, so both `IMsmtMessagePeer` and `IMsmtSessionPeer` expose them the same way. Each
peer owns exactly one tracker and hands the same instance to every connection or per-target sender it
creates, so a package outlives whatever carried it: a message peer's tracker survives a target sender being
evicted, and a session peer's survives a connection closing. A completed, cancelled or failed tag is queued
with its finish time and forgotten five minutes later, with no cap on how many are tracked. Expiry runs from
the owner's eviction loop, where it has one, and lazily from every tracker member regardless, so an expired
tag is never returned, and removes by instance so a newer send that reused the tag is never forgotten.
`MsmtPackage` wraps a `(tracker, tag, target, status)` tuple; its `Status` re-reads the tracker until it has
seen a final one, then latches. Tags identify a send within one tracker - one peer - so reusing one across
its connections replaces the earlier send's tracking, and the same tag on a different peer is unrelated.
