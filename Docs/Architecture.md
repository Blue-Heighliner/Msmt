# Architecture

This document explains the high-level design decisions behind [`Core/`](../Core)'s implementation of the
MSMT ICD - *why* it's built the way it is, not the class-by-class mechanics of *how*.

## MSMT is a TLS profile, not a protocol

The ICD defines MSMT as a fixed, narrowly pinned configuration of TLS 1.3 plus a thin message-framing
header - not a protocol of its own. That framing drove the whole design: there is no custom handshake,
no custom cryptography, and no custom trust model to build. The library's job is to pin TLS down to
exactly what the ICD requires (one TLS version, two cipher suites in a fixed order, mandatory mutual
certificate authentication, mandatory SNI) and add the message-boundary framing on top, then get out of
the way.

## Built directly on BouncyCastle's TLS engine

.NET's built-in `SslStream` was considered and rejected: it has no public way to pin the exact two-suite
cipher list the ICD requires in a fixed preference order, and no way to trigger a TLS 1.3 `KeyUpdate` on
demand (needed for `MessageWithRekeying`) without a full renegotiation. `Org.BouncyCastle.Tls` exposes
both as extension points, at the cost of implementing more of the client/server plumbing by hand.

Its blocking stream API was rejected in turn, because a write fails while a read is waiting on the same
stream and a blocked read or handshake ignores cancellation. A bidirectional connection needs a read
outstanding at all times, so the library drives BouncyCastle's non-blocking protocol API over an
asynchronous socket loop of its own. That also means no thread is ever blocked per connection, and every
timeout and cancellation is an ordinary asynchronous one instead of a socket being closed underneath a
blocked call.

## Two shapes of API, split by whether a connection is worth exposing

Message mode and message-with-rekeying are client-to-server exchanges: the sender sends and the listener
acknowledges, so an application only ever cares about a target and a message. `IMsmtMessagePeer` is built around
exactly that: it sends to targets, receives from targets, and keeps its connections to itself, since
exposing them would only offer operations the ICD gives them no use for. Because most message-handling
nodes both send and receive, one peer does both, and `StartListener` and `Send`/`Request` are independent
so an application can adopt whichever it needs.

Session mode is the one mode whose connection outlives a message and is negotiated up front, and the one
that can carry traffic both ways, so it is the one place where a connection is a thing the application
wants. `IMsmtSessionPeer` listens for and opens those connections, and all data moves through the
`IMsmtConnection` objects it hands out, in either direction. Keeping the two shapes separate means neither
carries the other's baggage: a peer has no connection lifecycle to explain, and a session connection has no
mode to choose.

What the two shapes still share - a listener, tagged-send tracking, disposal - lives on `IMsmtPeer`, which
both implement. It exists purely to avoid declaring that shared surface twice, not as a third, mode-agnostic
way to send or receive; nothing constructs or depends on a bare `IMsmtPeer` for its own sake.

The ICD describes the exchange as client to server only, so bidirectional sessions extend it. The only
place the wire allows is the existing header, so a message is told from an acknowledgement by matching the
message ID against the request in flight, and the three unused flag bits are
left zero. A peer that is not this library would not know to expect a message it did not ask for, which is
why only session connections, where both ends are this library, are bidirectional.

## Three connection lifecycle modes, trading security for overhead

The ICD defines Message, Message-with-Rekeying, and Session modes as points on a single tradeoff: a fresh
TLS handshake per message is the most secure (shortest-lived keys, least traffic-analysis surface) but the
most expensive; reusing one connection across many messages is cheaper but exposes more traffic to a
single TLS session. Offering all three, rather than picking one, lets each deployment choose its own point
on that curve based on its network's overhead tolerance and threat model - which is exactly the choice the
ICD leaves to implementers. A peer chooses between the first two through `MsmtMessagePeerOptions.RekeyLimit`:
`1`, the default, is Message mode - the ICD's own recommended default and the safest choice absent other
information - and anything higher is Message mode with rekeying. Choosing Session means using a session
peer instead.

## Hot, synchronous observables instead of callbacks, except for receiving

Every notification that is fundamentally "tell me every time X happens, for as long as I care" -
connecting, disconnecting, a tagged send's progress - is exposed as a plain `IObservable<T>` rather than an
`EventHandler` or a callback registered at construction time. This was chosen over `async` callbacks/
`Task`-returning hooks because observables model that repetition directly, while `Send`/`Request`'s own
`Task` results, and `IMsmtConnection.Wait`, already cover "await this one operation's outcome." Deliberately
choosing the simplest possible observable - hot, synchronous, no buffering or replay (`MsmtEventSubject<T>`)
- keeps the mental model equivalent to a C# multicast event, rather than pulling in a full
reactive-extensions dependency for behavior the library never needs (scheduling, backpressure, operators).

Receiving a message is deliberately not one of these: exactly one decision is ever needed per message, so
`Receiver` is a plain settable delegate property (`MsmtMessageReceiver`/`MsmtSessionReceiver`), awaited
once per message to decide how to acknowledge it, rather than an `IObservable<T>` with nothing to multicast
to.

A session connection itself carries none of the observable subjects: it takes callbacks at construction
time instead, and its owning `IMsmtSessionPeer` republishes the repeating ones into its own peer-wide
subjects, tagged with the connection they are about; `Receiver` is instead forwarded straight through,
since there is no subject to republish it into. This keeps a connection's own surface to just what it needs
to send and receive, and gives an application one place - the peer - to observe every connection at once,
rather than needing to subscribe to each connection individually as it appears.

## Pooled memory, ownership transfer

Legacy message-handling traffic is high-volume and latency-sensitive, so the API is built around
`IMemoryOwner<byte>` and pool-rented buffers rather than always allocating a fresh `byte[]` per message.
Ownership transfers at each hand-off (into `Send`/`Request`, out through `MsmtResponse`, into
`MsmtReceiveResult.Accept`/`.Reject`) so exactly one side is ever responsible for returning a buffer to its
pool. A received message's own payload is the exception: it is exposed to `Receiver` as a
`ReadOnlyMemory<byte>` over a buffer the connection still owns, and disposes once the handler's returned
`ValueTask` completes, since only one handler is ever invoked per message - there is no multicast to share
ownership across. A `ReadOnlyMemory<byte>` overload is offered everywhere pooled memory is accepted for an
outgoing payload, wrapping it in a non-owning shim, so callers who don't use pooling aren't forced to.

## Automatic on-demand connection lifecycle

Rather than requiring an application to explicitly open, track, and close a connection per remote target,
`IMsmtMessagePeer` creates a sender the first time a target is named, caches it for reuse, then evicts it
automatically once unused past `MsmtMessagePeerOptions.MaxIdleTime` or once `MaxConnectionCount` are open at once.
This mirrors how a message-handling application actually thinks about its peers - "send to this target" -
rather than requiring connection lifecycle management as a separate concern layered on top. The same two
rules apply symmetrically to connections a listener accepts, counted independently from the ones the peer
opens itself, and never interrupt a message in progress. "Unused" means no application traffic, and it
drops an idle sender even in Message mode, where no socket is open, so a peer that sends to many targets
once doesn't accumulate them. Tagged sends are tracked by the peer, not by a sender, so their packages
outlive the sender's eviction.

`IMsmtSessionPeer` deliberately has neither option: managing a session connection - deciding when it's no
longer needed and closing it - is the caller's job, since the caller is the one actually using it in both
directions and is in the best position to know. Only a handshake timeout, a stall, or the negotiated
session lifetime ends a connection there without the caller asking.

## Separate mechanisms for dropped, stalled, and long-lived connections

Detecting that a connection dropped, that a peer stalled, and how long a connection may live are three
different goals, and this implementation keeps them apart rather than stretching one timer over all of
them. The ICD only defines a session lifetime, a rekey count, and a Session-only keep-alive, so the rest
are implementation-defined and configured through `MsmtOptions`: the handshake, stall, and response
timeouts bound a peer that owes something, the pool policy above bounds a connection nobody uses, and the
negotiated lifetime and rekey count bound how long a connection may be reused. Keeping them separate is
what lets an idle Session connection be dropped for being unused without being mistaken for stalled, and a
stalled peer be dropped without waiting for an idle limit.

## Mandatory mutual TLS, offline revocation

The ICD makes mutual peer authentication non-negotiable, and this implementation follows suit: there is no
configuration that skips certificate exchange, and only certificate-based authentication is implemented
(the ICD's pre-shared-key alternative is not). Revocation checking is deliberately offline-only (checked
against any already-cached CRL, never fetched live), because many MSMT deployments run on networks without
CRL/OCSP connectivity, and Message Mode's per-message handshake would otherwise pay a live check's network
latency on every single send - a cost the ICD's own emphasis on sub-second handshake overhead argues
against.

## Concurrency: never block the caller

`Send` and `Request` only ever enqueue work; the actual TLS handshake, write, and acknowledgement read
happen on background loops the caller never touches. This keeps a slow or momentarily unreachable remote
peer from stalling the caller's thread, and lets sends queue and prioritize against each other rather than
serializing at the call site.
