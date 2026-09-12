# Architecture

This document explains the high-level design decisions behind [`Core/`](../Core)'s implementation of the
MSMT ICD — *why* it's built the way it is, not the class-by-class mechanics of *how*.

## MSMT is a TLS profile, not a protocol

The ICD defines MSMT as a fixed, narrowly pinned configuration of TLS 1.3 plus a thin message-framing
header — not a protocol of its own. That framing drove the whole design: there is no custom handshake,
no custom cryptography, and no custom trust model to build. The library's job is to pin TLS down to
exactly what the ICD requires (one TLS version, two cipher suites in a fixed order, mandatory mutual
certificate authentication, mandatory SNI) and add the message-boundary framing on top, then get out of
the way.

## Built directly on BouncyCastle's TLS engine

.NET's built-in `SslStream` was considered and rejected: it has no public way to pin the exact two-suite
cipher list the ICD requires in a fixed preference order, and no way to trigger a TLS 1.3 `KeyUpdate` on
demand (needed for `MessageWithRekeying`) without a full renegotiation. `Org.BouncyCastle.Tls` exposes
both as extension points, at the cost of implementing more of the client/server plumbing by hand.

## One peer-to-peer type, not separate client/server types

Most MSMT deployments both send and receive: a message-handling node relays traffic in both directions,
not strictly as a client or strictly as a server. Modeling that as one `IMsmtPeer` — internally a listener
plus a set of on-demand outgoing connections — means an application doesn't have to wire up and keep two
separate objects in sync just to participate in both directions. `StartListener` and `Send`/`Request` are
independent precisely so an application can adopt whichever subset it actually needs.

A consequence of the ICD's client-request/server-response model is that a connection a remote peer
initiated can only ever acknowledge what that peer sent — it can never be used to push something new back
down it. Rather than surfacing that as a caveat callers have to remember, the API simply never exposes a
way to send over a `Receiver` link at all: `Send`/`Request` always target a remote address, and the peer
transparently dials out to that remote peer's own receiver, reusing a cached connection when one exists.

## Three connection lifecycle modes, trading security for overhead

The ICD defines Message, Message-with-Rekeying, and Session modes as points on a single tradeoff: a fresh
TLS handshake per message is the most secure (shortest-lived keys, least traffic-analysis surface) but the
most expensive; reusing one connection across many messages is cheaper but exposes more traffic to a
single TLS session. Exposing all three as an `MsmtOperationMode` enum, rather than picking one, lets each
deployment choose its own point on that curve based on its network's overhead tolerance and threat model —
which is exactly the choice the ICD leaves to implementers. `Message` is the default because it's the
ICD's own recommended default and the safest choice absent other information.

## Hot, synchronous observables instead of callbacks

Every notification `IMsmtPeer` raises — links connecting, disconnecting, receiving, a tagged
send's progress — is exposed as a plain `IObservable<T>` rather than an `EventHandler` or a
callback registered at construction time. This was chosen over `async` callbacks/`Task`-returning hooks
because these notifications are fundamentally "tell me every time X happens, for as long as I care", which
observables model directly, while `Send`/`Request`/`Test`'s own `Task` results already cover "await this
one operation's outcome." Deliberately choosing the simplest possible observable — hot, synchronous,
no buffering or replay (`MsmtEventSubject<T>`) — keeps the mental model equivalent to a C# multicast event,
rather than pulling in a full reactive-extensions dependency for behavior the library never needs
(scheduling, backpressure, operators).

## Pooled memory, ownership transfer

Legacy message-handling traffic is high-volume and latency-sensitive, so the API is built around
`IMemoryOwner<byte>` and pool-rented buffers rather than always allocating a fresh `byte[]` per message.
Ownership transfers at each hand-off (into `Send`/`Request`, out through `Received`/`MsmtResponse`, into
`Accept`/`Reject`) so exactly one side is ever responsible for returning a buffer to its pool. A
`ReadOnlyMemory<byte>` overload is offered everywhere pooled memory is accepted, wrapping it in a
non-owning shim, so callers who don't use pooling aren't forced to.

## Automatic on-demand connection lifecycle

Rather than requiring an application to explicitly open, track, and close a connection object per remote
target, `IMsmtPeer` creates one the first time it's needed and caches it for reuse, then evicts it
automatically once idle past `MsmtOptions.MaxIdleTime` or once `MaxConnectionCount` on-demand connections
are open at once. This mirrors how a message-handling application actually thinks about its peers — "send
to this target" — rather than requiring connection lifecycle management as a separate concern layered on
top. The same two rules apply symmetrically to connections a listener accepts, counted independently from
the on-demand connections a peer creates itself, and never interrupt a connection with a message cycle
already in progress. Both rules have no practical effect on Message Mode's own single-exchange-per-connection
lifecycle, since neither ever gets a chance to apply before the exchange's own side (client or server)
already closes it; they matter for MessageWithRekeying and Session Mode connections, which sit open
between messages.

## Mandatory mutual TLS, offline revocation

The ICD makes mutual peer authentication non-negotiable, and this implementation follows suit: there is no
configuration that skips certificate exchange, and only certificate-based authentication is implemented
(the ICD's pre-shared-key alternative is not). Revocation checking is deliberately offline-only (checked
against any already-cached CRL, never fetched live), because many MSMT deployments run on networks without
CRL/OCSP connectivity, and Message Mode's per-message handshake would otherwise pay a live check's network
latency on every single send — a cost the ICD's own emphasis on sub-second handshake overhead argues
against.

## Concurrency: never block the caller

`Send` and `Request` only ever enqueue work; the actual TLS handshake, write, and acknowledgement read
happen on a background loop the caller never touches. This keeps a slow or momentarily unreachable remote
peer from stalling the caller's thread, and lets sends queue and prioritize against each other rather than
serializing at the call site.
