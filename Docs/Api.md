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
(`PackageChanged`, `Packages`, `GetPackage`), and disposal. Code that only needs those - starting a
listener, or looking up a package by tag without caring which kind of peer sent it - can depend on
`IMsmtPeer` directly; everything mode-specific (`Send`/`Request`/`Receiver` on a message peer; `Connect`,
`Connected`, `Disconnected`, `Connections`, `Receiver` on a session peer) stays on its own interface.

A tagged send's progress is surfaced through the `PackageChanged` `IObservable<T>`, since it is fundamentally
"notify me when X happens, possibly many times, for as long as I'm subscribed" - one queued payload can pass
through several statuses over its lifetime. Receiving a message is the opposite shape: exactly one decision
per message, awaited by whatever's sending it, so it is a single settable delegate property (`Receiver`,
of type `MsmtMessageReceiver` or `MsmtSessionReceiver`) instead, invoked and awaited once per message
received.

## Peer message flow

1. `peer.Send`/`peer.Request` is called with a target and a payload. The payload is only *enqueued*: the
   call returns immediately (or, for `Request`, returns a `Task<MsmtResponse>` that completes later).
2. The peer opens a connection to the target if its mode calls for one, exchanges the message, and
   `PackageChanged` publishes as the payload progresses - `Queued`, `Transmitting`, (for `Request` only)
   `PendingAcknowledgement`, then `Completed` - if the send was given a `Tag`. An untagged send skips this.
3. On the remote peer, its `Receiver` is invoked and awaited once its listener has read the full message,
   returning an `MsmtReceiveResult` that decides how to acknowledge it.
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

A peer's `Receiver` (`MsmtMessageReceiver` or `MsmtSessionReceiver`) is invoked and awaited once per
message, carrying the payload, the sender's address and verified identity (or, for a session connection,
the connection it arrived on), and whether the sender requested an acknowledgement
(`isResponseRequested`). It returns an `MsmtReceiveResult?` deciding how to acknowledge the message: `null`
if the sender never requested one - the only valid answer in that case, and it sends no acknowledgement at
all, not even a wire-level one - or `MsmtReceiveResult.Accept()`/`.Reject()`, optionally with a payload of
its own, when one was requested. Returning `null` when one was requested, or a non-null result when one
wasn't, is an error, closing the connection with an `InvalidOperationException`. Different connections may
invoke the same `Receiver` concurrently, so it must be safe to run at once for more than one message.

Deciding later - after awaiting something else first - is simply not returning until then: since `Receiver`
is awaited, whatever the handler awaits before it returns is what "later" means, rather than a separate
deferral mechanism. The sender's `ResponseTimeout` still bounds how long that may take. On a session
connection the connection keeps reading meanwhile, so a message this side sent is still acknowledged.

## Sending options and tracking

A single MSMT message or acknowledgement carries at most `MsmtLimits.MaxPayloadLength` bytes (16 MiB - 1),
the largest length its header can declare. `Send`/`Request` and `MsmtReceiveResult`'s payload-carrying
accept/reject overloads throw `ArgumentOutOfRangeException` for anything larger rather than transmitting a
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
