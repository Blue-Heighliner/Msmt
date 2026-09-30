# API Reference

The public API of [`Core/`](../Core) is two shapes that never mix. `IMsmtMessagePeer` sends messages directly to
targets and receives them directly from targets, using message mode or message-with-rekeying mode; its
connections are internal and never exposed. `IMsmtSessionPeer` listens for and opens session mode
connections, which are bidirectional, and data moves through the `IMsmtConnection` objects it hands out.
`IMsmtReachabilityChecker` is a third, narrower piece: a "ping" that checks a remote peer is reachable
without belonging to either shape. This document covers the *design and flow* of that surface using public
types only. Every public and internal type is also fully documented with XML doc comments in the source -
this document does not restate them.

## Shape of the API

`IMsmtMessagePeer.IFactory` and `IMsmtSessionPeer.IFactory` are the ways to obtain a peer or session peer,
each taking its own options record (`MsmtMessagePeerOptions`, `MsmtSessionPeerOptions`) that extends
`MsmtOptions` with what every endpoint shares: `MsmtCredentials` (certificate identity and trusted
authorities) and the handshake, stall, response and TCP keep-alive timeouts.
`IMsmtReachabilityChecker` needs no factory, since it carries no configuration of its own - its `Reach`
method takes a plain `MsmtOptions` directly, one call at a time.

The split follows the ICD. Message mode and message-with-rekeying are client-to-server exchanges: the
sender sends and the listener acknowledges, so a peer needs no notion of a connection at all. Session mode
is the one mode whose connection outlives a message and is negotiated up front, and it is the one that can
be used in both directions, so it is the only place a connection is worth handing to the application.

`IMsmtMessagePeer` and `IMsmtSessionPeer` both implement `IMsmtPeer`, which declares what they share and
nothing else: a listener (`Listener`, `StartListener`, `StopListener`), tagged-send tracking
(`PackageChanged`, `Packages`, `GetPackage`), the `Exceptions` observable, and disposal. Code that only needs those - starting a
listener, or looking up a package by tag without caring which kind of peer sent it - can depend on
`IMsmtPeer` directly; everything mode-specific (`Send`/`Request`/`Receiver` on a message peer; `Connect`,
`Connected`, `Disconnected`, `Connections`, `Receiver` on a session peer) stays on its own interface.

A tagged send's progress is surfaced through the `PackageChanged` `IObservable<T>`, since it is fundamentally
"notify me when X happens, possibly many times, for as long as I'm subscribed" - one queued payload can pass
through several statuses over its lifetime. Receiving a message is the opposite shape: exactly one decision
per message, so it is a single settable delegate property (`Receiver`, of type `MsmtMessageReceiver` or
`MsmtSessionReceiver`) instead, invoked once per message received.

## Peer message flow

1. `peer.Send`/`peer.Request` is called with a target and a payload. The payload is only *enqueued*: the
   call returns immediately (or, for `Request`, returns a `Task<MsmtResponse>` that completes later).
2. The peer opens a connection to the target if its mode calls for one, exchanges the message, and
   `PackageChanged` publishes as the payload progresses - `Queued`, `Transmitting`, (for `Request` only)
   `PendingAcknowledgement`, then `Completed` - if the send was given a `Tag`. An untagged send skips this.
3. On the remote peer, its `Receiver` is invoked once its listener has read the full message, handed an
   `IMsmtResponder` to acknowledge it with, if the sender asked for that.
4. Back on the sending side, `Request`'s `Task<MsmtResponse>` completes with the remote peer's decision and
   any payload it sent back. A plain `Send` never waits for this.

A send that fails ends `Failed` with the exception in `MsmtPackageChange.Exception`, but only if
it was tagged; an untagged `Send` has no way to report a failure, so use a tag or `Request` where it matters.

## Session flow

`IMsmtSessionPeer.StartListener` begins listening; `Connect` starts opening a connection and returns it
immediately, still `MsmtConnectionStatus.Connecting` - the TCP connect, TLS handshake, and session
negotiation all happen in the background. Await `IMsmtConnection.Wait` to find out whether it succeeded
(`true`) or the connection went straight to `Disconnected` without ever connecting (`false`), or dispose
the connection to abandon the attempt. On the accepting side, `Connected` publishes the same kind of
object once it is established; a connection this peer opened itself is not raised there, since `Connect`
already handed it straight back. From then on the two connection objects are symmetric: either can
`Send`/`Request`, and each side's messages arrive through the owning peer's `Receiver`. At most one message
per direction is in flight at a time, but the two directions do not wait for each other.

A connection carries no events of its own - every notification about it that is inherently repeated, such
as a tagged send's progress or it ending, is raised through the owning `IMsmtSessionPeer`'s `PackageChanged`
and `Disconnected`, each carrying the `IMsmtConnection` it is about; receiving a message instead goes
through the peer's single `Receiver`. `Disconnected` publishes once for every connection that ends,
however it ends - including one that never finished connecting, such as a client that never completes its
handshake or does not negotiate a session, or a remote peer that never answers one this peer opened - so
there is no separate "connect failed" event to watch in addition to it.

## Receiving and acknowledging

A peer's `Receiver` (`MsmtMessageReceiver` or `MsmtSessionReceiver`) is a plain synchronous delegate invoked
once per message, carrying the payload, the sender's address and verified identity (or, for a session connection,
the connection it arrived on), and an `IMsmtResponder?`. The payload is an `IMemoryOwner<byte>` the receiver
owns and disposes, as soon as it is finished with it. The responder is non-null exactly when the sender
requested an acknowledgement: the application answers it, exactly once, with `Accept()` or `Reject()`,
optionally with a payload of its own, and the call returns at once with the acknowledgement queued to be
written. It is `null` when none was requested, in which case no acknowledgement is sent at all, not even a
wire-level one. A second answer throws `InvalidOperationException`. Different connections may invoke the
same `Receiver` concurrently, so it must be safe to run at once for more than one message.

The receiver returning means nothing about whether the message was answered, and this is not checked:
answering is up to the application, from any thread and at any time, inside the receiver or long after it
returned, and it is the sender's `ResponseTimeout` that bounds a message that never is. The connection
does stay open until a requested message has been answered, so answering later still reaches the sender,
and an unanswered one keeps its connection from being evicted as idle. Messages on one connection are still
handed to `Receiver` one at a time and in order, so a receiver that blocks holds up the ones behind it.

## Sending options and tracking

A single MSMT message or acknowledgement carries at most `MsmtLimits.MaxPayloadLength` bytes (16 MiB - 1),
the largest length its header can declare. `Send`/`Request` and `IMsmtResponder`'s payload-carrying
accept/reject methods throw `ArgumentOutOfRangeException` for anything larger rather than transmitting a
message the remote peer would reject as malformed; bundle application-level messages to stay within it.

`MsmtSendOptions` governs one queued payload: an optional `Tag` to identify it across `PackageChanged` and
`GetPackage`, a `Priority` among other queued payloads to the same destination, and an optional `Dscp`
marking for QoS. `IMsmtPackage` (returned by `GetPackage`, and carried on `Packages`/`PackageChanged`) is
the handle for a tagged send: its `Status` (an `MsmtSendStatus`) always reflects the send's current
progress, and `Cancel()` requests best-effort cancellation. Both `IMsmtMessagePeer` and `IMsmtSessionPeer`
track their own packages (`Packages`/`GetPackage`, declared on the shared `IMsmtPeer`) independently of
which connection or pooled sender carried a tagged send, so a package stays available after that connection
or sender is gone, until it has been finished for five minutes; `IMsmtConnection` itself tracks no packages
of its own.

## Failures

Timeouts surface as `TimeoutException`, from `Request` and `IMsmtReachabilityChecker.Reach` to their
callers and through the events to observers: a handshake that never completes, a transfer that stalls, or
a message that is never acknowledged. A cancelled `CancellationToken` surfaces as
`OperationCanceledException`. A remote peer that rejects a message's header or does not support the mode
fails the request with `InvalidOperationException`.

Failures that don't end a connection have nowhere else to surface, so both peers publish them on
`IMsmtPeer.Exceptions`: a `Receiver` that throws (which leaves its connection and later messages alone), a
subscriber to `PackageChanged` or `Disconnected` that throws (which does not stop the other subscribers), a
connection accepted by a message peer's listener that failed to establish, a failure evicting a connection,
and a failed accept in the listener. One that ends a connection is reported through that connection's own
`Disconnected` instead.
