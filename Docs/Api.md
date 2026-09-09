# API Reference

The public API of [`Core/`](../Core) is a single peer-to-peer type, `IMsmtPeer`, backed by a factory for
DI-free or DI-based construction. This document covers the *design and flow* of that surface — how the
pieces fit together and the order events happen in — using public types only. For runnable examples of
specific scenarios, see [Usage.md](Usage.md); for why the API is shaped this way, see
[Architecture.md](Architecture.md); for how `IMsmtPeer`'s real implementation works internally, see
[Implementation.md](Implementation.md). Every public and internal type is also fully documented with XML
doc comments in the source — this document does not restate them.

## Shape of the API

`IMsmtPeerFactory.Create(MsmtOptions)` is the only way to obtain an `IMsmtPeer`. `MsmtOptions` carries
one node's `MsmtCredentials` (certificate identity and trusted authorities) plus the connection-behavior
defaults applied uniformly to every connection that peer accepts or creates.

A peer is symmetric: it manages at most one listener (started explicitly via `StartListener`) for
connections other peers open to it, and a separate outgoing connection per remote target it has been
asked to send to, created on demand the first time `Send`/`Request` names that target and cached for
reuse afterward. Listening and sending are entirely independent — a peer that never calls `StartListener`
can still send to others, and one that does can simultaneously receive from peers it never explicitly
dialed. This is why there is one `IMsmtPeer` type rather than separate client/server types: most
applications need to do both at once, and the ICD's messages/acknowledgements pair up naturally regardless
of which side of a given TCP connection they flow over.

Everything a peer does — connecting, disconnecting, receiving, and a tagged send's progress — is surfaced
through eight `IObservable<T>` properties rather than `async` callbacks or `Task`-returning hooks. This
keeps the shape of "notify me when X happens, possibly many times, for as long as I'm subscribed" separate
from "await this one operation's result", which `Send`/`Request`/`Test` already cover on their own.

## Message flow

Sending and receiving a single request-response message touches the API roughly in this order:

1. `peer.Send`/`peer.Request` is called with a target and a payload. If no connection to that target
   exists yet, one is created and cached; either way the payload is only *enqueued* — the call returns
   immediately (or, for `Request`, returns a `Task<MsmtResponse>` that completes later).
2. `Linking` publishes once the new connection begins its TLS handshake (skipped if a connection was
   already cached and reused).
3. `Linked` publishes once that handshake completes and the connection is secured. If this is the
   target's first active link, `Connected` also publishes, carrying the `IMsmtConnection` pairing this
   peer's sending and receiving directions with that target.
4. `PackageChanged` publishes as the queued payload progresses — `Queued` → `Transmitting` → (for
   `Request` only, since a plain `Send` never waits for an acknowledgement) `PendingAcknowledgement` →
   `Completed` — if the send was given a `Tag`. An untagged send skips this step entirely.
5. On the remote peer, the same `Linking` → `Linked` → (if this is that peer's target's first active
   link) `Connected` sequence fires for its own accepted link - symmetric with steps 2-3 above, just for
   the receiving direction instead of the sending one. Once that completes, `Received` publishes once its
   listener has read the full message. A subscriber may accept or reject it via
   `MsmtReceivedEventArgs.Responder`, or let it be accepted automatically once every subscriber has run.
6. Back on the sending side, `Request`'s `Task<MsmtResponse>` completes with the remote peer's decision and
   any payload it sent back. A plain `Send` never waits for this and has no `MsmtResponse` to observe.
7. Eventually the link closes — immediately after this exchange in `Message` mode, after some number of
   further exchanges in `MessageWithRekeying`/`Session` mode, or once idled out or evicted. `Unlinked`
   publishes when it does, and `Disconnected` once the connection holds no links in either direction.

`LinkFailed` instead of `Linked` publishes if step 2's handshake never completes.

## Connections and links

An `IMsmtConnection` is this peer's logical relationship with one remote target (matched by address and
port, ignoring TLS server name): up to one outgoing `Sender` link and up to one incoming `Receiver` link,
either of which may be `null` if that direction isn't currently open. Both are `IMsmtLink`s — `Kind`
distinguishes which direction one is, and `Connection` reaches back to the pairing. `IMsmtConnection.Drop`/
`Disconnect` always act on both links at once; to end just one direction, call `Drop`/`Disconnect` on that
link itself (e.g. `connection.Sender?.Drop()`).

Because MSMT's client-request/server-response model means an incoming connection can only ever acknowledge
what was sent to it, never push something new, a peer always replies to a remote peer by dialing back out
to it as a client, never by writing to a connection that peer itself opened. `Send`/`Request` are the only
way to transmit; `IMsmtConnection`/`IMsmtLink` only ever expose `Drop`/`Disconnect` to end a link, not to
send over it.

`peer.ActiveConnections` snapshots every connection currently holding at least one link;
`peer.GetActiveConnection` looks one up by target, but only while it holds exactly one linked link (see
[Usage.md#managing-connections](Usage.md#managing-connections) for the exact matching rule and why both-
or-neither returns `null`).

## Receiving and acknowledging

`Received` carries the payload, the `Receiver` link it arrived on, whether the sender requested an
acknowledgement (`IsResponseRequested`), and an `IMsmtResponder` to decide with. A subscriber calls
`Responder.Accept()`/`Reject()` synchronously, optionally with a payload of its own; if no subscriber
decides, the message is accepted automatically once every subscriber has run. Calling `Responder.Defer()`
opts out of that automatic acceptance, handing the decision to whatever else holds the responder —
including code running after the deferring subscriber has already returned.

`Accept()`/`Reject()` only work when `IsResponseRequested` is `true` (i.e. the sender used `Request`, not
`Send`) — calling either otherwise throws. A plain `Send`'s wire acknowledgement always reports success,
since there is no acknowledgement through which the application could report otherwise.

## Sending options and tracking

A single MSMT message or acknowledgement carries at most `MsmtLimits.MaxPayloadLength` bytes (16 MiB - 1),
the largest length its header can declare. `Send`/`Request` and `IMsmtResponder`'s payload-carrying
accept/reject overloads throw `ArgumentOutOfRangeException` for anything larger rather than transmitting a
message the remote peer would reject as malformed; bundle application-level messages to stay within it.

`MsmtSendOptions` governs one queued payload: an optional `Tag` to identify it across `PackageChanged` and
`GetPackage`, a `Priority` among other queued payloads on the same connection, and an optional `Dscp`
marking for QoS. `IMsmtPackage` (returned by `GetPackage`, and carried on `Packages`/`PackageChanged`) is
the handle for a tagged send: its `Status` (an `MsmtSendStatus`) always reflects the send's current
progress, and `Cancel()` requests best-effort cancellation.

## Types at a glance

Full member-level documentation lives in each type's XML doc comments; this table is only a map of where
each concept lives, grouped by role.

| Role | Types |
|---|---|
| Entry points | `IMsmtPeer` / `MsmtPeer`, `IMsmtPeerFactory` / `MsmtPeerFactory`, `MsmtOptions`, `MsmtCredentials`, `MsmtServiceCollectionExtensions.AddMsmt` |
| Addressing | `MsmtTarget`, `MsmtNameTarget` |
| Connections and links | `IMsmtConnection`, `IMsmtLink`, `MsmtLinkType`, `MsmtIdentity` |
| Sending | `MsmtSendOptions`, `MsmtSendStatus`, `IMsmtPackage`, `MsmtResponse`, `MsmtLimits` |
| Receiving | `MsmtReceivedEventArgs`, `IMsmtResponder`, `MsmtResponseKind` |
| Connection lifecycle modes | `MsmtOperationMode` |
| Events | `MsmtLinkingEventArgs`, `MsmtLinkedEventArgs`, `MsmtLinkFailedEventArgs`, `MsmtUnlinkedEventArgs`, `MsmtPackageChangedEventArgs`, `MsmtConnectedEventArgs`, `MsmtDisconnectedEventArgs`, `MsmtObservableExtensions` |

## See also

- [Usage.md](Usage.md) — runnable examples for each of the scenarios above, plus disposal and DI.
- [Architecture.md](Architecture.md) — the design decisions behind this shape.
- [Implementation.md](Implementation.md) — how `MsmtPeer` actually implements `IMsmtPeer` internally.
- [ICD.md](ICD.md) — the full Mercury Secure Message Transport Interface Control Document (v1.2).
