# Connection management

How `MsmtPeer` (`IMsmtPeer`) tracks, counts, and tears down connections, and how `MsmtConnection`
(`IMsmtConnection`) - the pairing of at most one outgoing `MsmtClient` (`Sender`) and one incoming
`MsmtServerConnection` (`Receiver`) for the same remote target - exposes ending them.

`MsmtPeer` owns one `MsmtServer` (created on `StartListener`) and a
`ConcurrentDictionary<(string Host, int Port), MsmtConnection>` pairing each remote target's cached
outgoing `MsmtClient` (if any) with its currently accepted incoming `MsmtServerConnection` (if any).
`MsmtServer` and `MsmtClient` expose their own notifications as plain internal C# events
(`EventHandler<T>`); `MsmtPeer` subscribes to those in `StartListener`/`CreateSender` and republishes each
one through its own `MsmtEventSubject<T>` fields, which is what its public `IObservable<T>` properties
(`Linking`, `Linked`, `LinkFailed`, `Received`, `Unlinked`, `PackageChanged`, `Connected`, `Disconnected`)
actually expose.

## The connection registry

`MsmtPeer.connections` is keyed by the remote target's address and port alone, ignoring TLS server name -
matching `IMsmtPeer.GetActiveConnection`'s own matching rule. In the common case only one side of an
`MsmtConnection` is ever populated for a given key, since an accepted socket's observed remote port is
ephemeral and essentially never equal to a port this peer separately dials out to - but the pairing still
applies whenever it does occur, e.g. once a peer's own request happens to originate from the exact port the
other side later connects to.

`MsmtConnection` exposes `EnsureSender`/`AttachReceiver`/`RemoveSender`/`RemoveReceiver`, called only by
`MsmtPeer` as links connect and disconnect, to keep `SenderEngine`/`ReceiverEngine` in sync; a connection
is removed from the registry (`RemoveConnectionIfEmpty`) once both are empty. `EnsureSender` uses a
double-checked lock (`senderLock`) around the factory that builds a new `MsmtClient`, guaranteeing that
factory - and so the client's background loops - runs exactly once per connection even under concurrent
callers racing `Send`/`Request` for the same target.

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

## Ending a connection

`IMsmtConnection.Drop`/`.Disconnect` always act on both links at once: `Drop` closes each attached link
immediately (`MsmtClient.Drop`/`MsmtServerConnection.Drop`, both synchronous), while `Disconnect` awaits
their teardown first - for a `Receiver`, by forcibly closing its socket (via the closure captured in
`AttachDisconnector`) and awaiting its `HandleConnection` task's completion; for a `Sender`, via
`MsmtClient.DisposeAsync`. `IMsmtLink.Drop`/`.Disconnect` expose these same two methods on a single link
directly, letting a caller end just one direction without touching the other.

## Eviction

`MsmtOptions.MaxConnectionCount` needs a cross-connection comparison to pick a victim, so `MsmtPeer` runs a
background `EvictionLoop` task (a `PeriodicTimer` ticking every second) that enforces it independently for
each direction: `EvictExcessSenders`/`EvictExcessReceivers` count all connections with a `SenderEngine`/
`ReceiverEngine` respectively, and - only among the ones currently `IsIdle` - evict the oldest-activity
excess via `EvictConnection`/`EvictReceiver`. Both re-check `IsIdle` immediately before evicting, since a
candidate selected moments earlier may have started a cycle since. `EvictConnection` removes the client
from the registry before awaiting its `DisposeAsync`, so a racing `Send` to the same target creates a
fresh client rather than reusing one already being torn down; `EvictReceiver` just calls the accepted
connection's own `Disconnect`, since - unlike a cached `MsmtClient` - its registry cleanup already happens
reactively through the normal `Unlinked` event chain. This never interrupts a connection with a
send/message cycle currently queued or in progress - each direction's own idle-detection (see the Sending
and Receiving components) feeds `IsIdle`.

## Disposal

`MsmtPeer.Dispose()` cancels `disposalCancellation`, disposes the server and every cached sender
immediately without waiting, then disposes the cancellation source. `DisposeAsync()` instead cancels,
awaits the eviction loop, then disposes every sender (awaited, one at a time) *before* disposing the
server - in that order because a still-open link's read loop on the other side may only unblock once this
side closes it, so closing this peer's own on-demand links first avoids it outliving links it could have
released earlier.

## Reachability check (`Test`)

`IMsmtPeer.Test` bypasses `MsmtClient`/`MsmtServer` entirely: it opens its own raw `TlsClientProtocol` over
a `TcpClient`, connects via `MsmtTlsClient`, writes a header with the `ReachabilityCheck` flag and a
zero-length payload, reads back the acknowledgement, and returns whether it both `Acknowledges` the request
and carries the same flag - then always closes the connection. This is a one-shot, connection-per-call
operation independent of `MsmtOptions.Mode`, matching the ICD's description of reachability checking as a
standalone diagnostic rather than part of the normal send path. It is served on the remote side by the same
generic `ReachabilityCheck`-flag handling in `MsmtServer`'s connection loop that also answers a
reachability-flagged message sent through the ordinary `MsmtClient` send path.

## Memory ownership

Received payloads and response payloads are always rented from `MemoryPool<byte>.Shared`, delivered as an
`IMemoryOwner<byte>` on `MsmtReceivedEventArgs.Payload`/`MsmtResponse.Payload`. Ownership transfers to
whoever handles them, who must dispose it once done. `IMsmtPeer.Send`/`.Request` and
`IMsmtResponder.Accept`/`.Reject`'s `IMemoryOwner<byte>` overloads take ownership the same way - disposed
once the send/acknowledgement completes, successfully or not. `NonOwningMemoryOwner` backs each type's
`ReadOnlyMemory<byte>` overload, wrapping the input via `MemoryMarshal.AsMemory` with a no-op `Dispose()`,
so a caller with an ordinary buffer doesn't need to manage a pool itself. `EmptyMemoryOwner.Instance` is a
shared, always-empty singleton used for payload-less acknowledgements and keep-alive reachability checks,
avoiding a fresh empty allocation for either.
