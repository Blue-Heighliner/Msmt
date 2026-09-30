# Session peer

`MsmtSessionPeer` (`IMsmtSessionPeer`) both accepts and opens session connections: an accepted socket and a
dialed one are tracked in the same registry and treated identically everywhere but which callback routed
them there, since either direction may send to and receive from the caller once established.

## Connecting and accepting

`Connect` dials through the connector as an initiator in session mode, which also runs the negotiation, and
returns the connection immediately, still `Connecting` - the connector attaches its TLS channel
synchronously before `Connect` returns and runs the handshake and negotiation in the background, so a
caller that disposes the connection right away always has something to abort. The peer tracks the
connection before returning it; if the peer was disposed concurrently, it disposes the connection too
rather than leaving it to establish onto a peer that no longer wants it.

Each accepted socket runs on its own task through the connector, as an acceptor that requires a session: a
client that does not negotiate one is answered with the mode-unsupported flags and closed. The peer awaits
the new connection's `Wait()`; only once it resolves `true` does the peer publish it to `Connected`. If a
`Connected` subscriber throws, the connection is closed gracefully (`Evict`, not an abrupt `Dispose` -
which could otherwise race the negotiation reply just written to the wire) and reported through
`Disconnected` like any other connection that never became usable, since there is no separate
connect-failed event to route it through instead.

## Callback-routed events

A connection carries none of its own events: `Connect`/accept pass the peer's own `OnReceived`,
`OnPackageChanged` and `OnDisconnected` methods into the connection's constructor, and the connection
invokes them directly as things happen. `OnPackageChanged` and `OnDisconnected` republish into the peer's
own subject, tagging the payload with the connection it came from (`MsmtPackageChange.Connection`,
`MsmtDisconnection.Connection`) so a subscriber watching the peer-wide observable can still tell
connections apart; `OnDisconnected` also removes the connection from the registry, whether it disconnected
after connecting or never connected at all. `OnReceived` instead forwards straight to whatever `Receiver`
is currently set, passing along the payload and responder - there is no subject here, since exactly one
decision is needed per message. A `Receiver` that throws is caught there and published on the peer's
`Exceptions`, leaving the connection and later messages alone; the same subject also carries the
listener's failed accepts and subscribers to `PackageChanged` or `Disconnected` that throw.

## Tagged-send tracking

Unlike a connection opened by `MsmtMessagePeer`'s target sender, a session connection is a real object the
caller keeps and uses directly, so `Packages`/`GetPackage` (declared on the shared `IMsmtPeer`) are backed
by one `MsmtPackageTracker` the peer owns and hands to every connection it creates or accepts. A package is
tracked independently of the connection that carried it, and outlives that connection disconnecting; a
connection itself tracks no packages of its own.

## No automatic disconnection

The peer runs no eviction loop and has no `MaxIdleTime` or `MaxConnectionCount` option - those are
`MsmtMessagePeerOptions` only. A session connection stays open, however long unused, until the caller
disposes it, its handshake or a transfer times out, or its negotiated lifetime ends. Managing a session
connection's lifetime, since it is what the caller actually sends and receives over in both directions, is
left entirely to the caller rather than guessed at by a pool policy.

## Disposal

`Dispose` closes every tracked connection immediately, without waiting; `DisposeAsync` stops the listener
first, then disposes every connection and waits for each to finish. Either is idempotent.
