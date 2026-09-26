# Connection health

How a connection is kept honest: noticing that it dropped, giving up on a peer that stalled, and bounding
how long it lives or sits unused. These are three separate goals with separate mechanisms, and each applies
differently per connection lifecycle mode. The ICD only specifies the session lifetime, the requirement that
idle connections disconnect, the Session-only keep-alive, and the rekey count, so everything else is
configured through `MsmtOptions`.

## Detecting a dropped connection

- **TCP keep-alive** (`MsmtOptions.TcpKeepAliveTime`, `MsmtProtocol.ApplyTcpKeepAlive`) is set on every
  socket a peer creates, accepts, or opens for `Test`, so the operating system detects a host or path that
  vanished without closing. It is best-effort, like DSCP marking. It says nothing about a peer whose
  application has stalled but whose host is up.
- **The server** always has a read outstanding, so a closed connection is noticed immediately.
- **The client** never reads while idle. `MsmtClient` therefore polls the socket (`IsSocketAlive`) from its
  maintenance check, once a second, and again before reusing a cached connection, treating a socket that is
  readable with nothing to read, or in error, as closed. It raises `Unlinked` promptly and the next send
  opens a fresh connection instead of failing on the dead one. Unread bytes count as alive, since they may
  be a TLS record rather than the end of the stream, so a close that arrives behind unread TLS records is
  only guaranteed to be discovered by the next send failing. A reset, and the server's own idle-eviction
  closes in `Session` and `MessageWithRekeying` mode, are detected by the poll.
- **Keep-alives** are a Session-only ICD mechanism (see below), not the drop detector.

## Detecting a stalled peer

A stall is a peer that owes something and is late; idleness is a peer that owes nothing. `MsmtWatchdog`
bounds one phase at a time and, on expiry, closes the connection's socket, the only way to interrupt
BouncyCastle's blocking handshake and reads. The failure is then reported as a `TimeoutException`, while
caller cancellation stays an `OperationCanceledException`. `MsmtWatchedStream` wraps the raw transport
beneath TLS and reports every completed read or write, so a stall timeout restarts on progress and a large
message on a slow but working link is never cut off.

| Phase | Timeout | Measured as |
|---|---|---|
| TCP connect and TLS handshake, on the client, the listener, and `Test` | `HandshakeTimeout` | total time |
| Listener: first message header after the handshake | `HandshakeTimeout` | total time, since a client only connects to send |
| Rest of a message once its first byte arrives, and writing any message or acknowledgement | `StallTimeout` | time without progress |
| Client: from the last request byte written until the acknowledgement header is read | `ResponseTimeout` | total time |

`ResponseTimeout` also matters because a client processes its sends one at a time: an unresponsive peer
would otherwise block every later send to that target. A listener never times out a deferred responder;
that is application policy, and the remote client's `ResponseTimeout` protects it.

## Bounding lifetime and unused connections

- **`Session`**: the negotiated lifetime. `MsmtClient` closes the connection when it ends even with no
  traffic, from its maintenance check. `MsmtServer` closes it too, from `CloseAtSessionExpiry`, after a
  short grace period so the client closes first, and only if the connection is idle at that moment. A
  connection mid-message is closed by the check that follows the message.
- **`MessageWithRekeying`**: the ICD bounds it by rekey count, not time. Both sides enforce
  `MsmtOptions.RekeyLimit`: the client closes after that many messages, and the listener closes an accepted
  connection that never negotiated a session after serving that many.
- **`Message`**: bounded by the exchange itself.
- **Unused connections**: `MsmtOptions.MaxIdleTime` is a pool policy, not a stall detector. A caller who
  never manages connections by hand relies on it to drop the ones they stopped using, in every mode and on
  both sides. It counts application traffic only: `MsmtClient.LastActivityUtc` and
  `MsmtServerConnection.LastActivityUtc` exclude keep-alives, reachability checks, and session negotiation,
  so a Session connection that only exchanges keep-alives is still evicted. `MsmtPeer`'s eviction loop
  enforces it, once a second, alongside `MaxConnectionCount`. An idle client is removed from the pool and
  disposed, even in `Message` mode where no socket is open, so unused targets and their background loops go
  away; a listener connection is closed and reports a `TimeoutException`. Only a connection that is idle at
  that moment is evicted, and the check is repeated under the same lock a send takes, so a send that just
  arrived keeps its connection.

## Keep-alives

In `Session` mode `MsmtClient` sends a reachability check once the connection has gone a randomized
interval, chosen per connection between `KeepAliveMinInterval` and `KeepAliveMaxInterval` (3 to 5 minutes
by default, per the ICD), without any message. They keep the connection alive on the wire and exercise the
response timeout. They do not count as application traffic, so `MaxIdleTime` still applies: set it to
`null` to hold a session open until its lifetime ends.

## The maintenance check

While a connection is open, `MsmtClient` queues a maintenance item every second, run by the same loop that
sends, so it never races a send. It closes an expired Session, closes a connection found dead, and queues a
keep-alive when one is due. It is not counted as a send, so it never keeps a client from being idle.
