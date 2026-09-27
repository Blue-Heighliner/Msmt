# Connection health

How a connection is kept honest: noticing that it dropped, giving up on a peer that stalled, and bounding
how long it lives or sits unused. These are three separate goals with separate mechanisms, and each applies
differently per mode. The ICD only specifies the session lifetime, the requirement that idle connections
disconnect, the Session-only keep-alive, and the rekey count, so everything else is configured through
`MsmtOptions`.

## Detecting a dropped connection

Every connection keeps a read outstanding, so a close is noticed the moment it happens: the TLS channel's
read loop sees the end of the stream, or the reset, and the connection closes, invoking its `onDisconnected`
callback, which a session peer republishes as `Disconnected`. That needs no polling and no probe messages,
and it is why a connection that a peer closed while idle is found before the next send instead of by it
failing. `TcpKeepAliveTime` additionally enables TCP keep-alive on every socket, so the operating system
also detects a host or path that vanished without closing. It is best-effort, like DSCP marking, and says
nothing about a peer whose application stalled but whose host is up.

## Detecting a stalled peer

A stall is a peer that owes something and is late; idleness is a peer that owes nothing. Each phase in
which a peer owes something is bounded, and none of them needs a socket to be closed to interrupt it, since
the channel is asynchronous:

| Phase | Timeout | Measured as |
|---|---|---|
| TCP connect and TLS handshake, for connections opened or accepted, and `IMsmtReachabilityChecker.Reach` | `HandshakeTimeout` | total time |
| Acceptor: first message after the handshake | `HandshakeTimeout` | total time, since a client only connects to send |
| Rest of a message once its first byte arrives | `StallTimeout` | time without progress |
| Sending any chunk to the peer | `StallTimeout` | time without progress |
| From the last request byte written until the acknowledgement arrives | `ResponseTimeout` | total time |

A read stalls when no bytes arrive for the stall timeout, restarting on each chunk, so a large message on a
slow but working link is never cut off. `ResponseTimeout` also matters because sends are processed one at a
time: an unresponsive peer would otherwise block every later send to it. A `Receiver` that awaits something
else before deciding is never timed out on the side that holds it; that is application policy, and the
remote sender's `ResponseTimeout` protects it. A timeout closes the connection and is reported as a
`TimeoutException`.

## Bounding lifetime and unused connections

- **Session**: the negotiated lifetime. The initiator closes when it ends, even with no traffic, from its
  maintenance tick; the acceptor closes a silent connection shortly after, so the initiator closes first. A
  connection mid-message is closed by the check that follows the message.
- **Message with rekeying**: the ICD bounds it by rekey count, not time. Both sides enforce
  `MsmtMessagePeerOptions.RekeyLimit`: the sender closes after that many messages, and the listener closes
  an accepted connection that never negotiated a session after serving that many.
- **Message**: bounded by the exchange itself.
- **Unused connections**: `MsmtMessagePeerOptions.MaxIdleTime` is a pool policy, not a stall detector, and
  exists only on `IMsmtMessagePeer` - a caller who never manages connections by hand relies on it to drop
  the per-target senders and accepted connections it stopped using, in both Message modes and on both
  sides. It counts application traffic in either direction; keep-alives, reachability checks and session
  negotiation do not count. Only a connection idle at that moment is evicted, and the check is repeated just
  before, so a message that just arrived keeps its connection. `IMsmtSessionPeer` has no such option: a
  session stays open however long it sits unused, until the caller disposes it or its lifetime ends, since
  managing it is the caller's job.

## Keep-alives

In session mode the initiator sends a reachability check once the connection has gone a randomized
interval, chosen per connection between `KeepAliveMinInterval` and `KeepAliveMaxInterval` (3 to 5 minutes
by default, per the ICD), without any message. It keeps the connection alive on the wire and exercises the
response timeout, so a peer that stopped answering is found even while nothing else is being sent. It does
not count as application traffic.
