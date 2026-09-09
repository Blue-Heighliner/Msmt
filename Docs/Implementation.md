# Implementation

This document explains how the internal `MsmtPeer` class actually implements the public `IMsmtPeer`
interface (see [Api.md](Api.md)), and how its internal collaborators work together to do so. For *why* it
is built this way, see [Architecture.md](Architecture.md); for usage examples, see [Usage.md](Usage.md).

## Components

| Type | Role |
|---|---|
| `MsmtPeer` (`IMsmtPeer`) | The public-facing peer-to-peer layer. Owns one `MsmtServer` (created on `StartListener`) and a `ConcurrentDictionary<(string Host, int Port), MsmtConnection>` pairing each remote target's cached outgoing `MsmtClient` (if any) with its currently accepted incoming `MsmtServerConnection` (if any). |
| `MsmtConnection` (`IMsmtConnection`) | The internal counterpart to the public `IMsmtConnection`: a mutable pairing of at most one `MsmtClient` (`Sender`) and one `MsmtServerConnection` (`Receiver`) for the same remote target, owned exclusively by `MsmtPeer`. |
| `MsmtClient` (`IMsmtLink`) | Sends messages to a single remote server over one TLS connection, created and torn down per `MsmtOptions.Mode`. Backs each connection's `Sender` link. |
| `MsmtServer` | Listens for inbound TLS connections and dispatches each one's messages through `Received`, acknowledging once a subscriber decides via `IMsmtResponder`, or automatically once every subscriber has run without deciding - unless one called `Defer()`, in which case it awaits that responder's eventual decision, reading no further message on the connection meanwhile. Backs a peer's receiver. |
| `MsmtServerConnection` (`IMsmtLink`) | One connection `MsmtServer` accepted, backing a connection's `Receiver` link; unlike `MsmtClient`, it cannot send - messages back to that remote peer always go out as a new client connection dialed to its own receiver. |
| `MsmtPackage` (`IMsmtPackage`) | A single tagged payload queued on an `MsmtClient`, backing `IMsmtPeer.Packages`/`GetPackage` and `MsmtPackageChangedEventArgs.Package`. |
| `MsmtProtocol` / `MsmtHeader` / `MsmtMessageFlags` | Shared wire-level helpers: the fixed 10-byte header (version, flags, message ID, length) and pooled-buffer read/write helpers used by both `MsmtClient` and `MsmtServer`. |
| `MsmtBcCryptography` / `MsmtTlsClient` / `MsmtTlsServer` / `RekeyableTlsClientProtocol` | The TLS 1.3 layer, built directly on `Org.BouncyCastle.Tls`. |
| `MsmtEventSubject<T>` | A minimal hand-rolled hot `IObservable<T>` - no buffering or replay, synchronous multicast to every current subscriber - backing each of `MsmtPeer`'s public `IObservable<T>` properties. |

`MsmtServer` and `MsmtClient` expose their own notifications as plain internal C# events
(`EventHandler<T>`); `MsmtPeer` subscribes to those in `StartListener`/`CreateSender` and republishes each
one through its own `MsmtEventSubject<T>` fields, which is what its public `IObservable<T>` properties
(`Linking`, `Linked`, `LinkFailed`, `Received`, `Unlinked`, `PackageChanged`, `Connected`, `Disconnected`)
actually expose.

## The connection registry

`MsmtPeer.connections` is a `ConcurrentDictionary<(string Host, int Port), MsmtConnection>` keyed by the
remote target's address and port alone, ignoring TLS server name - matching `IMsmtPeer.GetActiveConnection`'s
own matching rule. In the common case only one side of an `MsmtConnection` is ever populated for a given
key, since an accepted socket's observed remote port is ephemeral and essentially never equal to a port
this peer separately dials out to - but the pairing still applies whenever it does occur, e.g. once a
peer's own request happens to originate from the exact port the other side later connects to.

`MsmtConnection` exposes `EnsureSender`/`AttachReceiver`/`RemoveSender`/`RemoveReceiver`, called only by
`MsmtPeer` as links connect and disconnect, to keep `SenderEngine`/`ReceiverEngine` in sync; a connection
is removed from the registry (`RemoveConnectionIfEmpty`) once both are empty. `EnsureSender` uses a
double-checked lock (`senderLock`) around the factory that builds a new `MsmtClient`, guaranteeing that
factory - and so the client's background loops - runs exactly once per connection even under concurrent
callers racing `Send`/`Request` for the same target.

## Listener path

`StartListener` disposes any previous `MsmtServer`, creates a new one, and wires its plain C# events into
`MsmtPeer`'s own `MsmtEventSubject<T>` fields and into the connection registry:

- `Linking` attaches the newly accepted `MsmtServerConnection` to (or creates) the target's
  `MsmtConnection` via `AttachIncomingLink`/`AttachReceiver`, then republishes `Linking` - so a `Linking`
  subscriber already sees the connection registered. `MsmtServerConnection.IsConnected` (and so
  `IMsmtConnection.Receiver`) stays `false`/`null` until the handshake actually completes, mirroring
  `MsmtClient.IsConnected`'s symmetric meaning for the sending direction; `MsmtServer.HandleConnection`
  calls `MarkConnected()` right before raising `Linked` once the handshake succeeds.
- `Linked` republishes, then checks whether this makes the connection newly `Connected` - identical to how
  the sender side's own `Linked` handler works, so `Connected` fires at the same point in the handshake
  lifecycle regardless of which direction just completed it.
- `LinkFailed`/`Unlinked` detach the link (`DetachIncomingLink`/`RemoveReceiver`) before republishing, and
  clean up an now-empty connection entry. A `LinkFailed` link never reached `Linked`, so it was never
  counted by `Connected` and needs no corresponding `Disconnected` check.
- `Received`/`PackageChanged` (the latter never actually raised by `MsmtServer` - see the components
  table) are republished as-is.

`MsmtServer.Host` binds a `TcpListener` and starts a background `AcceptLoop` task. Each accepted
`TcpClient` is handed to its own `HandleConnection` task immediately, so one client's slow TLS handshake
never delays accepting the next; `HandleConnection` performs the TLS accept via `MsmtTlsServer`, raising
`Linking` beforehand and `Linked`/`LinkFailed` once the handshake resolves, then loops reading one message
at a time (never more than one in flight, per the ICD's request/acknowledge model):

1. Read the fixed 10-byte header. A malformed header gets an `InvalidPreambleOrModeUnsupported`
   acknowledgement and the connection closes.
2. If the `SessionModeNegotiation` flag is set, `RespondToSessionNegotiation` handles it (only honored as
   the very first message on the connection - see [Connection lifecycle modes](#connection-lifecycle-modes)
   below) and the loop continues without touching `Received` at all.
3. If the `ReachabilityCheck` flag is set, the payload is echoed straight back with the same flag and the
   payload is never passed to `Received`.
4. Otherwise, `RespondToMessage` raises `Received` with a fresh `MsmtResponder`, waits for a decision (via
   `IsDeferred`/`WaitForDecision` if `Defer()` was called, or applies the automatic-accept default), and
   writes the acknowledgement. `IMsmtResponder.Accept`/`Reject` only work when an acknowledgement was
   actually requested (see [Receiving and acknowledging](Api.md#receiving-and-acknowledging)); a plain
   `Send` always gets `MessageSuccess` set on its wire response, since there is no ack-based path through
   which the application could have rejected it.
5. If a `Session` connection's negotiated lifetime has elapsed, the loop returns and the connection closes;
   `Message`/`MessageWithRekeying` connections are instead left to the client side to close.

## Sender path

`Send`/`Request` both call `GetOrCreateConnection`, which resolves or creates the target's `MsmtConnection`
and calls `EnsureSender` with a factory (`CreateSender`) that builds a new `MsmtClient`, wires its events
into `MsmtPeer`'s subjects exactly like the listener path, attaches its cache-removal callback
(`AttachToCache`, invoked by `Drop`/`Disconnect`/eviction to remove the connection from the registry), and
calls `Connect` - which starts `MsmtClient`'s two background loops (`ProcessQueueLoop`, `KeepAliveLoop`).

Inside `MsmtClient`, `Send`/`Request` never touch the network directly - they call `Enqueue`, which wraps
the payload and options into a `PendingSend` record, records it in `activeSends` if tagged, adds it to a
`PriorityQueue<PendingSend, (int NegatedPriority, long Sequence)>` (highest priority first, FIFO within a
priority via the monotonically increasing `sendSequence`), raises `PackageChanged` as `Queued`, and signals
a `SemaphoreSlim`. A single background `ProcessQueueLoop` task dequeues and sends one item at a time, so a
slow or blocked send never starves a higher-priority one waiting behind it. For each item, `SendOverConnection`:

1. Calls `EnsureConnection`, which reuses the existing `RekeyableTlsClientProtocol` for
   `MessageWithRekeying`/still-valid `Session` connections, or closes any stale one and opens a fresh TLS
   connection otherwise - raising `Linking`/`Linked`/`LinkFailed` around the handshake, and negotiating a
   session lifetime immediately afterward for `Session` mode (see below).
2. Applies `MsmtSendOptions.Dscp` to the raw socket, if set, immediately before writing.
3. Writes the header and payload, raising `PackageChanged` as `Transmitting`, then (if an acknowledgement
   was requested) as `PendingAcknowledgement`.
4. Reads and validates the acknowledgement header and payload, completing the `Request`'s
   `TaskCompletionSource<MsmtResponse>` if one exists (or disposing the response payload if not, for a
   plain `Send`).
5. Applies the mode-specific post-send action (see below), then raises `PackageChanged` as `Completed` or
   `Cancelled`.

Any exception or cancellation encountered mid-frame closes the connection (`CloseConnection`) to avoid
desynchronizing a later send on the same client. Since BouncyCastle's TLS stream doesn't honor a
cancellation token on a write or read already blocked, `SendOverConnection` registers a callback on its
linked token (covering this item's own `CancellationToken`, `Cancel(tag)`'s `CancelSource`, and this
client's own disposal) that force-closes the raw socket the instant any of the three fires
(`ForceCloseSocket`) - the only way any of them actually interrupts a send in flight rather than leaving it
blocked until the remote peer eventually responds. The resulting `IOException` is reported to the caller as
an `OperationCanceledException` whenever the linked token was in fact cancelled, rather than a confusing
"connection closed" error.

## Connection lifecycle modes

Implemented entirely inside `MsmtClient.SendOverConnection`/`EnsureConnection`:

- `Message` (default) - `EnsureConnection` always opens a fresh `RekeyableTlsClientProtocol`;
  `SendOverConnection` closes it again immediately after the acknowledgement is read.
- `MessageWithRekeying` - the same connection is reused across sends. After each acknowledgement,
  `SendOverConnection` calls `protocol.Rekey()` (a TLS 1.3 `KeyUpdate`) unless `messagesSinceConnect` has
  reached `MsmtOptions.RekeyLimit`, in which case the connection is closed and the next send opens a new
  one.
- `Session` - `EnsureConnection` negotiates a lifetime via `NegotiateSession` immediately after
  connecting (a `SessionModeNegotiation`-flagged message carrying the proposed lifetime in seconds as its
  payload) and reuses the connection for every subsequent send until `sessionExpiresAtUtc` passes, at
  which point the next send transparently reconnects. A background `KeepAliveLoop` task (ticking every 15
  seconds, checking against a randomized 3-5 minute idle threshold recomputed per connection) sends an
  empty `ReachabilityCheck` payload whenever a `Session` connection has gone that long without any
  application traffic, so it isn't dropped for inactivity between application sends.

On the server side, `MsmtServer.RespondToSessionNegotiation` only honors a negotiation request as the very
first message on a connection (per the ICD); it agrees to the lesser of the client's proposed lifetime and
`MsmtHostOptions.MaximumSessionLifetime`, and the accepting `HandleConnection` loop closes the connection
once that agreed lifetime elapses.

## `Connected`/`Disconnected` bookkeeping

`MsmtPeer.activeConnections` is a `ConcurrentDictionary<IMsmtConnection, byte>` used purely as a set,
backing `ActiveConnections`. `CheckConnected(link)` adds the link's connection and raises `Connected` only
if `OtherLink(link)` (the opposite direction) is currently `null` - i.e. this is the connection's first
active link. `CheckDisconnected` mirrors this for removal. Both directions are checked from the same call
site, `RaiseLinked` (not `Linking`), since a link only becomes "counted" once its handshake actually
succeeds - symmetric for `Sender` and `Receiver` alike, backed by `MsmtClient.IsConnected`/
`MsmtServerConnection.IsConnected` sharing the same "handshake completed, not yet closed" meaning. A
`LinkFailed` link, of either kind, never reached `Linked` and so was never counted, meaning `RaiseLinkFailed`
never needs to trigger `CheckDisconnected`.

## Packages

`MsmtPackage` wraps a `(client, tag, status)` triple. Its `Status` getter is lazy: until it has observed a
final status (`Completed`/`Cancelled`) it re-reads `client.GetStatus(tag)` on every access, then latches.
`MsmtClient` tracks a tagged send's status in two structures: `activeSends` (the still-outstanding
`PendingSend`, removed once finished, backing `Cancel`) and `sendStatuses` (every tag's last known status,
including finished ones, backing `GetStatus`/`GetPackage`). To bound `sendStatuses`' otherwise-unbounded
growth for a long-lived client given a uniquely tagged send per message, a completed or cancelled tag is
queued into `completedSendTags`, and the oldest is evicted from `sendStatuses` once more than 10,000
(`maxTrackedCompletedSendStatuses`) have accumulated - so `GetStatus`/`GetPackage` keeps working for every
in-flight or recently finished send and only forgets a tag once far enough behind more recent completions.

## Eviction

`MsmtOptions.MaxIdleTime`/`MaxConnectionCount` apply symmetrically to both directions - the outgoing
`MsmtClient` connections a peer creates on demand and the incoming connections its `MsmtServer` accepts -
and, for either direction, never interrupt a connection with a send/message cycle currently queued or in
progress. Both mechanisms have no practical effect on a `Message` Mode connection, which always closes
itself immediately after each cycle before either rule ever gets a chance to apply; they can affect a
`MessageWithRekeying` or `Session` connection sitting open between messages.

`MaxIdleTime` is enforced per-connection rather than by a shared polling loop, so detection is prompt
rather than bounded by a fixed sweep interval:

A single `CancellationTokenSource`/`Task.Delay` timer throws `ArgumentOutOfRangeException` beyond roughly
49.7 days (`MsmtProtocol.MaxTimerDuration`, `uint.MaxValue - 1` milliseconds), so neither side ever waits
out a configured `MaxIdleTime` in one timer - each chains as many clamped-length waits as needed
(`MsmtProtocol.ClampToMaxTimerDuration`) to honor an arbitrarily long value exactly, rather than crashing
or firing early:

- `MsmtClient` tracks `LastActivityUtc` (when its last send completed) and `IsIdle` (`outstandingSends ==
  0` - no send currently queued or in flight). After each completed send that leaves the connection open
  (`MessageWithRekeying`/`Session`), `ScheduleIdleCheck` starts a one-shot, clamped `Task.Delay` that, once
  elapsed, enqueues an idle-check placeholder item (`PendingSend.IsIdleCheck`). Routing the check through
  the same single-consumer queue as real sends - rather than closing the connection directly from the timer
  - guarantees it can never race a concurrently processing send: by the time `HandleIdleCheck` runs,
  nothing else is mid-cycle. If the clamped wait was shorter than the configured `MaxIdleTime`, or a send
  refreshed `LastActivityUtc` while the check was already in flight, `HandleIdleCheck` finds the connection
  not yet genuinely idle and calls `ScheduleIdleCheck` again for whatever time remains, so it only ever
  closes a connection once truly idle for the full configured duration. `idleCheckScheduled` debounces this
  across a burst of sends - a second call while one is already pending is a no-op, since the pending one
  will observe the latest activity and reschedule itself if needed.
- `MsmtServerConnection` tracks the same two concepts (`LastActivityUtc`/`IsIdle`, mirrored via
  `MarkIdle`/`MarkBusy`) across the gap between message cycles. `MsmtServer.HandleConnection` calls
  `MarkIdle` immediately before each "wait for the next header" read and `MarkBusy` immediately after one
  arrives, so only that gap ever counts as idle. Because BouncyCastle's TLS stream falls back to a blocking
  byte[]-based `ReadAsync` overload it never overrides (see `MsmtServer.DisposeAsync`), a cancelled token
  alone cannot interrupt that blocked read; `RunIdleTimeout` instead loops a chain of clamped `Task.Delay`
  waits against the true deadline and, only once genuinely elapsed, closes the underlying socket directly -
  mirroring shutdown's own technique - forcing the blocked read to fail with an `IOException` that
  `HandleConnection` reports as a `TimeoutException` (disambiguated from an ordinary disconnect via a
  shared single-element flag `RunIdleTimeout` sets immediately before closing the socket). The loop is
  cancelled and awaited immediately once the read settles - before touching the header - closing the same
  race window `MsmtClient` closes via its queue.

`MaxConnectionCount` still needs a cross-connection comparison to pick a victim, so `MsmtPeer` runs a
background `EvictionLoop` task (a `PeriodicTimer` ticking every second) that enforces it independently for
each direction: `EvictExcessSenders`/`EvictExcessReceivers` count all connections with a `SenderEngine`/
`ReceiverEngine` respectively, and - only among the ones currently `IsIdle` - evict the oldest-activity
excess via `EvictConnection`/`EvictReceiver`. Both re-check `IsIdle` immediately before evicting, since a
candidate selected moments earlier may have started a cycle since. `EvictConnection` removes the client
from the registry before awaiting its `DisposeAsync`, so a racing `Send` to the same target creates a
fresh client rather than reusing one already being torn down; `EvictReceiver` just calls the accepted
connection's own `Disconnect`, since - unlike a cached `MsmtClient` - its registry cleanup already happens
reactively through the normal `Unlinked` event chain.

## Disposal

`MsmtPeer.Dispose()` cancels `disposalCancellation`, disposes the server and every cached sender
immediately without waiting, then disposes the cancellation source. `DisposeAsync()` instead cancels, awaits
the eviction loop, then disposes every sender (awaited, one at a time) *before* disposing the server - in
that order because a still-open link's read loop on the other side may only unblock once this side closes
it, so closing this peer's own on-demand links first avoids it outliving links it could have released
earlier. `MsmtServer.DisposeAsync` itself closes every accepted connection's raw socket directly (rather
than relying on cancellation alone) because BouncyCastle's TLS stream falls back to a blocking `byte[]`
`ReadAsync` overload it never overrides internally, which a cancelled token alone cannot interrupt.
`MsmtClient.DisposeAsync` mirrors this: it calls `ForceCloseSocket` immediately after cancelling
`disposalCancellation` and *before* awaiting `processingLoop`, since awaiting first would otherwise hang
forever were the loop currently blocked inside `SendOverConnection`'s own write or acknowledgement read -
only `ForceCloseSocket` can unblock that, not the cancelled token alone. `ProcessQueueLoop` remains the
sole owner of the `connection` field and its `Unlinked` bookkeeping either way: `ForceCloseSocket` only
touches the raw socket, letting `SendOverConnection`'s own exception handling perform the actual
`CloseConnection` once the resulting exception unwinds on its own thread.

`IMsmtConnection.Drop`/`.Disconnect` mirror this same split at the link level, always acting on both links
at once: `Drop` closes each attached link immediately (`MsmtClient.Drop`/`MsmtServerConnection.Drop`, both
synchronous), while `Disconnect` awaits their teardown first - for a `Receiver`, by forcibly closing its
socket (via the closure captured in `AttachDisconnector`) and awaiting its `HandleConnection` task's
completion; for a `Sender`, via `MsmtClient.DisposeAsync`. `IMsmtLink.Drop`/`.Disconnect` expose these same
two methods on a single link directly, letting a caller end just one direction without touching the other.

## Reachability check (`Test`)

`IMsmtPeer.Test` bypasses `MsmtClient`/`MsmtServer` entirely: `MsmtPeer.Test` opens its own raw
`TlsClientProtocol` over a `TcpClient`, connects via `MsmtTlsClient`, writes a header with the
`ReachabilityCheck` flag and a zero-length payload, reads back the acknowledgement, and returns whether it
both `Acknowledges` the request and carries the same flag - then always closes the connection. This is a
one-shot, connection-per-call operation independent of `MsmtOptions.Mode`, matching the ICD's description
of reachability checking as a standalone diagnostic rather than part of the normal send path. It is served
on the remote side by the same generic `ReachabilityCheck`-flag handling in `MsmtServer.HandleConnection`
that also answers a reachability-flagged message sent through the ordinary `MsmtClient` send path.

## Wire framing

Every message and acknowledgement is a fixed 10-byte `MsmtHeader` (1-byte version, 1 reserved byte, a
16-bit `MsmtMessageFlags`, a 16-bit random message ID, and a 32-bit big-endian length) followed by an
opaque payload of that length. `MsmtHeader.SupportedVersion` is `3` (MSMT v1.2, with the Message ID
field); `IsWellFormed()` rejects any other version, any bit outside `DefinedFlags`, or a length beyond
`MaxLength` (`0xFFFFFF`, exposed publicly as `MsmtLimits.MaxPayloadLength`), which
`MsmtProtocol.ValidatePayloadLength` also enforces outbound - at `Send`/`Request` and `MsmtResponder.Decide`,
before a payload is queued or written - so an oversized payload fails fast with an
`ArgumentOutOfRangeException` instead of being transmitted and then rejected as malformed mid-connection;
`Acknowledges` matches a response header back to its request by version and
message ID. `MsmtMessageFlags` is a `[Flags]` enum whose bits are combined for higher-level outcomes - e.g.
`SessionModeAccepted` is `SessionModeNegotiation | MessageSuccess`. `MsmtProtocol.ReadPooled` reads a
payload directly into a buffer rented from `MemoryPool<byte>.Shared` (sliced to the exact requested length
via `SlicedMemoryOwner`, since a pool may return a larger buffer than requested), so a received message
never costs an extra allocation beyond the pool's own; `MsmtProtocol.ReadExact` is the underlying
loop-until-filled primitive both `ReadPooled` and raw header reads use.

## TLS layer

- `MsmtTlsClient`/`MsmtTlsServer` extend BouncyCastle's `DefaultTlsClient`/`DefaultTlsServer`, pinning
  `GetSupportedVersions()` to TLS 1.3 only and `GetSupportedCipherSuites()` to exactly
  `TLS_CHACHA20_POLY1305_SHA256` and `TLS_AES_256_GCM_SHA384`, in that order, per the ICD.
  `MsmtTlsClient` always presents a `ServerName` (SNI) extension from `MsmtNameTarget.ServerName`, and its
  nested `Authentication` verifies the server's certificate (`MsmtBcCryptography.IsTrusted`) and matching
  server name (`MatchesServerName`) before recording `ServerIdentity`, and supplies this client's own
  credentials when the server requests mutual authentication. `MsmtTlsServer.ProcessClientExtensions`
  rejects a handshake missing a server name extension or presenting an unacceptable one (see
  `MsmtHostOptions.RequireFullyQualifiedHostname`/`IsValidHostname`), and `NotifyClientCertificate`
  verifies the client's certificate the same way before recording `ClientIdentity`.
- `MsmtBcCryptography` bridges .NET's `X509Certificate2`-based `MsmtCredentials` to BouncyCastle's own
  certificate/key types (`ToBcIdentity`), and implements the trust check both sides apply to the peer's
  presented chain (`IsTrusted`): built against a `CustomRootTrust` `X509Chain` seeded with the configured
  trusted authorities, requiring an RSA key of at least 2048 bits (`MinimumRsaKeySizeBits`), with
  revocation checked offline (against any already-cached CRL, never fetched live) and a missing/unavailable
  revocation source tolerated rather than treated as a failure. Only RSA identity certificates are
  supported - `ToBcIdentity` throws `NotSupportedException` for anything else - and
  `SelectSignatureAlgorithm` limits signature algorithm negotiation to the three RSA-PSS schemes
  (`rsa_pss_rsae_sha256/384/512`) BouncyCastle's engine can sign with directly. `MatchesServerName`
  implements RFC 6125 matching against a certificate's Subject Alternative Name entries (or Common Name,
  if no SAN extension is present), including a single leftmost wildcard DNS label.
- `RekeyableTlsClientProtocol` extends `TlsClientProtocol` purely to expose `Send13KeyUpdate`
  (otherwise `protected`) as a public `Rekey()` method, since `MessageWithRekeying` needs to trigger a key
  update between messages without a full handshake.

## Concurrency model

- `MsmtClient` never sends on the caller's thread; see [Sender path](#sender-path) above for its queue
  and single `ProcessQueueLoop`. Both `ProcessQueueLoop` and `KeepAliveLoop` run for the client's whole
  lifetime, started from `Connect` and stopped via `disposalCancellation`.
- `MsmtServer` runs one background `AcceptLoop` task handing each accepted `TcpClient` to its own
  `HandleConnection` task; see [Listener path](#listener-path) above.
- `MsmtPeer` adds the background `EvictionLoop` task described in [Eviction](#eviction).

Both `MsmtClient` and `MsmtServer`/`MsmtPeer` expose an immediate, non-waiting `Dispose()` alongside a
graceful `DisposeAsync()`; see [Disposal](#disposal) above for the specific ordering each uses.

## Memory ownership

Received payloads and response payloads are always rented from `MemoryPool<byte>.Shared`
(`MsmtProtocol.ReadPooled`), delivered as an `IMemoryOwner<byte>` on `MsmtReceivedEventArgs.Payload`/
`MsmtResponse.Payload`. Ownership transfers to whoever handles them, who must dispose it once done.
`IMsmtPeer.Send`/`.Request` and `IMsmtResponder.Accept`/`.Reject`'s `IMemoryOwner<byte>` overloads take
ownership the same way - disposed once the send/acknowledgement completes, successfully or not.
`NonOwningMemoryOwner` backs each type's `ReadOnlyMemory<byte>` overload, wrapping the input via
`MemoryMarshal.AsMemory` with a no-op `Dispose()`, so a caller with an ordinary buffer doesn't need to
manage a pool itself. `EmptyMemoryOwner.Instance` is a shared, always-empty singleton used for payload-less
acknowledgements and keep-alive reachability checks, avoiding a fresh empty allocation for either.

## Observable plumbing

`MsmtEventSubject<T>` backs every one of `MsmtPeer`'s public `IObservable<T>` properties: `Subscribe` adds
the observer to an internal list (under a `Lock`) and returns an `Unsubscriber` that removes it again on
`Dispose`; `Publish` takes a snapshot of the current subscriber list and invokes `OnNext` on each in
subscription order, synchronously, on the calling thread - no buffering, no replay for a late subscriber,
and no scheduling of any kind. This is what makes a `Received` subscriber's synchronous call to
`Responder.Accept()`/`Reject()`/`Defer()` (see [Listener path](#listener-path) above) work: the publish
call that raised `Received` is still on the stack, on the same connection's `HandleConnection` task, when
the subscriber decides.

## See also

- [Architecture.md](Architecture.md) — the design decisions behind these mechanics.
- [Api.md](Api.md) — the public API's design and flow.
- [Usage.md](Usage.md) — usage examples for common scenarios.
- [ICD.md](ICD.md) — the full Mercury Secure Message Transport Interface Control Document (v1.2).
- [`AGENTS.md`](../AGENTS.md) — coding conventions used throughout this project.
