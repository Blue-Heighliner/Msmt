# Connection

`MsmtConnection` is one MSMT connection in any role and any mode: the TLS channel plus everything MSMT
layers on it. The session `IMsmtConnection` handed out by `IMsmtSessionPeer` is one, and so is every
connection an `IMsmtMessagePeer` opens or accepts internally. Where they differ is decided by an
`MsmtConnectionSettings` record from whoever creates it, not by separate classes. It carries no events of
its own - its owner supplies plain delegates (`onReceived`, `onPackageChanged`, `onDisconnected`) at
construction time instead, which it invokes directly as things happen, so the owner can republish them
peer-wide tagged with the connection they came from.

## Role and mode

The *role* says who opened the TCP connection: the initiator or the acceptor. It decides who negotiates a
session, sends keep-alives, and triggers key updates; those stay with the initiator, as in the ICD. The
*mode* is message, rekeying or session. An initiator is told its mode. An acceptor learns it from what the
initiator sends first: a session negotiation makes it a session, anything else is a message or rekeying
connection, which the acceptor cannot tell apart and does not need to, since it treats both the same. An
acceptor is also told whether to accept a session (a session peer requires one, a message peer refuses
one), and a message that breaks that rule is answered with the mode-unsupported flags and the connection is
closed. This is independent of `Direction` (`Incoming`/`Outgoing`), which only says which side opened the
TCP connection, for a caller that cares without needing to know role or mode.

## Establishing

`MsmtConnector.Dial`/`Accept` construct the connection, synchronously attach its TLS channel, and return it
immediately, still `Connecting` - attaching before returning closes the window where a caller that disposes
the connection right away would have nothing to abort. The TCP connect (when dialing), TLS handshake, and
MSMT's own first exchange then run in the background (`RunDial`/`RunAccept`), bounded by the handshake
timeout, and end by calling `CompleteHandshake` or `FailToConnect` on the connection.

`CompleteHandshake` negotiates: an initiator in session mode sends the negotiation and reads the agreed
lifetime; an acceptor reads the first message, where a negotiation is answered and sets the expiry, and any
other message is held and delivered first once the connection starts. Success moves `Status` from
`Connecting` to `Connected`, resolves `Wait()` with `true`, and starts the loops; any failure, from
`CompleteHandshake` or from `FailToConnect`, closes the connection without ever reaching `Connected`, and
resolves `Wait()` with `false`, so it looks to `Wait()`'s caller exactly like any other connection that
closed before finishing.

## The loops

- The **reader loop** parses messages off the channel and routes each. A message whose ID matches the
  request in flight completes it. A reachability check is echoed straight back. Anything else is a request
  from the remote side and is queued for the handler loop, or, on a connection that does not accept
  requests, is a protocol violation that closes it. The reader never waits on the application, so an
  acknowledgement this side is waiting for is never stuck behind a slow subscriber.
- The **handler loop** takes those requests one at a time, invokes and awaits `onReceived`, and, if the
  message requested an acknowledgement, writes the `MsmtReceiveResult` it returned; a handler that keeps
  awaiting before returning therefore delays only later messages from the same sender. A message that did
  not request one gets no acknowledgement written at all, and `onReceived` must return `null` for it - the
  reverse mismatch (a non-null result for one that wasn't requested, or `null` for one that was) is a
  contract violation that closes the connection. The queue between the two loops is small and bounded; a
  peer that sends faster than the application handles simply stops being read, and flow control pushes back
  on it.
- The **send queue** (`MsmtOutbox`) sends this side's requests, one at a time, if `MsmtConnectionSettings.
  ProcessSends` is `true` (the default). Each `Exchange` writes the message and, only if it requested an
  acknowledgement (or is a keep-alive or reachability check, which always get one), waits for it under the
  response timeout - a plain send that never requested one completes as soon as it is written, without
  waiting for or expecting any reply. Cancelling one already written, or its reply timing out, closes the
  connection, since a late reply could no longer be matched to anything. A connection whose queue isn't
  started - a message peer's target sender drives its connections through `Exchange` directly instead -
  still answers `Exchange` calls; only its own internal queueing is skipped.
- In a session, the **maintenance loop** ticks once a second: the initiator queues a keep-alive when the
  connection has been silent for its randomized interval, and closes at the end of the lifetime; the acceptor
  closes a silent connection after the lifetime plus a short grace period so the initiator closes first.

Because every loop is asynchronous, a connection holds no thread of its own, and an idle one costs nothing
beyond its buffers. Writes from the send queue, the handler, and echoes all pass through one write lock, so
each message goes out whole.

## Closing

`Dispose()` and `DisposeAsync()` both funnel through one `Terminate`, which the first caller wins, differing
only in whether the channel closes gracefully (`DisposeAsync`) or is aborted (`Dispose`). Either way,
`Terminate` cancels the loops, closes or aborts the channel, fails the request in flight with the reason,
disposes the send queue so queued sends are cancelled, moves `Status` to `Disconnected`, resolves `Wait()`
with `false` if it hadn't already resolved, invokes `onDisconnected` once with the reason, and completes an
internal `Closed` task `DisposeAsync` awaits before returning. A remote close is an ordinary end with no
exception; a reset, a timeout or a protocol violation is the exception. A connection that decided to close
itself, such as message mode after its exchange, or a rekey limit, does so right after the response, so a
following send finds it closed before writing anything and can safely use a new one.

## After each exchange

An initiator in message mode closes; in rekeying mode it triggers a key update, or closes once
`RekeyLimit` messages have been exchanged; in session mode it closes if the lifetime is over. An acceptor
that has not negotiated a session closes after `RekeyLimit` messages, as its initiator does too. A
session acceptor closes at the end of the lifetime, after the message in progress.
