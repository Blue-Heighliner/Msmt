# Message peer

`MsmtMessagePeer` (`IMsmtMessagePeer`) sends to targets and receives from targets in message mode and
message-with-rekeying mode, and keeps every connection behind that to itself.

## Sending

A peer keeps one `MsmtTargetSender` per remote target (address and port), created the first time a send
names it. Each sender owns a send queue that outlives any connection, because in message mode every message
gets a connection of its own and the next queued message must not depend on the previous one's. The sender
does what the mode calls for when its queue hands it a message: it uses its current connection if that is
still open, and otherwise dials a new one through the connector (with `MsmtConnectionSettings.ProcessSends`
set to `false`, so the connection's own send queue never starts - the sender drives it directly through
`Exchange` instead), then exchanges the message over it. In message mode the connection closes itself after
the exchange; in rekeying mode it triggers a key update and is reused until it reaches the rekey limit, and
any of these closes surface as the connection no longer being open, so the next message dials again. A
connection that finishes its own cycle between being chosen and used throws before anything is written, and
the sender retries on a new one, so nothing is lost or sent twice.

`Send`/`Request` reserve the sender for their target, queue the message, and release the reservation.
Reserving is what keeps the pool's eviction from retiring a sender between finding it and queueing on it.

## Receiving

The peer's listener accepts sockets and hands each to a task of its own, so one client's stalled handshake
never delays accepting the next. Each accepted connection is an acceptor that accepts requests and refuses a
session negotiation, passing the peer's own `OnReceived` callback so a received message forwards straight
to whatever `Receiver` is currently set, passing along the payload and responder - a connection carries no
such event of its own to forward instead. With no `Receiver` set, the payload is disposed and a requested
acknowledgement is accepted. The connection is tracked
only for eviction and disposal. A connection that fails to establish is dropped and its failure published on the peer's
`Exceptions`, since a peer exposes no connections to report it on. A `Receiver` that throws is published
there too, and leaves the connection and later messages alone. Stopping the listener stops accepting and leaves connections
already accepted alone.

## Eviction and disposal

One loop, ticking every second, enforces the idle and count limits for both directions through
`MsmtEviction`: idle limits are judged per sender or connection by its own activity; the count limit drops
the least recently used idle ones, counted separately for senders and accepted connections, and only among
those not in the middle of a message. Every candidate is re-checked just before it is evicted, and a sender
is retired under a lock that reservations also take. An evicted sender's queue and connection are disposed,
including in message mode where no socket is open, so unused targets do not accumulate; the peer's package
tracker outlives it. A failure evicting one thing is swallowed so eviction never stops for the life of the
peer. The same loop expires finished packages.

Disposal is idempotent. `DisposeAsync` stops the eviction loop, closes the peer's own outgoing connections
before the listener, so it does not outlive links it could have released earlier, then waits for the
accepted ones. A `Send`/`Request` racing disposal that created a sender afterward disposes it itself.

## Tagged-send tracking

`Packages`/`GetPackage`, declared on the shared `IMsmtPeer`, are backed by one `MsmtPackageTracker` the peer
owns and hands to every `MsmtTargetSender` it creates, so a package is tracked independently of whichever
sender or connection carried it and outlives either being evicted or closed.
