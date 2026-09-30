# Usage

Runnable examples of `IMsmtMessagePeer`, `IMsmtSessionPeer` and `IMsmtReachabilityChecker` in different situations.

## Loading credentials

```csharp
using BlueHeighliner.Msmt;

MsmtCredentials credentials = MsmtCredentials.FromPemFiles("identity.pem", "identity.key", "ca.pem");
```

`FromPemFiles`/`FromPemStreams`/`FromPemText` all accept an optional trusted-authority argument; when
omitted, trusted authorities are instead derived from the identity certificate's own PEM text/chain.
`MsmtCredentials.FromStore` loads the identity certificate from the system's X.509 certificate store
instead, by subject name.

## Minimal peer

```csharp
using BlueHeighliner.Msmt;

// RequireFullyQualifiedHostname is disabled here only because this example connects by loopback IP
// address rather than a real DNS hostname; leave it enabled (the default) whenever a hostname is used.
IMsmtMessagePeer peer = new IMsmtMessagePeer.Factory().Create(new MsmtMessagePeerOptions { Credentials = credentials, RequireFullyQualifiedHostname = false });

// The handler owns payload and must dispose it; responder is null because this message never requested an acknowledgement.
peer.Receiver = (source, identity, payload, responder) =>
{
    using (payload)
    {
        Console.WriteLine(Encoding.UTF8.GetString(payload.Memory.Span));
    }

    return default;
};

peer.StartListener(port: 5000);
peer.Send(new MsmtTarget { Host = "127.0.0.1", Port = 5000 }, "hello"u8.ToArray());
```

A peer sends in Message mode by default (`RekeyLimit = 1`): a fresh TLS connection for every message. Set
`RekeyLimit` above `1` for Message mode with rekeying, reusing one connection, with a TLS key update between
messages, until that many messages have gone over it. A peer never exposes its connections; it sends to
targets and receives from targets.

## Peer request-response

```csharp
MsmtCredentials serverCredentials = MsmtCredentials.FromPemFiles("server.pem", "server.key", "ca.pem");
MsmtCredentials clientCredentials = MsmtCredentials.FromPemFiles("client.pem", "client.key", "ca.pem");
IMsmtMessagePeer.IFactory peerFactory = new IMsmtMessagePeer.Factory();

// --- Receiving side ---
IMsmtMessagePeer server = peerFactory.Create(new MsmtMessagePeerOptions { Credentials = serverCredentials, RequireFullyQualifiedHostname = false });

server.Receiver = (source, identity, payload, responder) =>
{
    using (payload)
    {
        Console.WriteLine($"{identity.Subject} sent {Encoding.UTF8.GetString(payload.Memory.Span)}");
    }

    // responder is non-null only for a message that requested an acknowledgement (the sender used Request,
    // not Send), and that message must be answered exactly once - Accept() or Reject(), optionally with a
    // payload of its own - by you: nothing checks, and the sender waits until its ResponseTimeout otherwise.
    responder?.Accept();
};

server.StartListener(port: 5000);

// --- Sending side ---
IMsmtMessagePeer client = peerFactory.Create(new MsmtMessagePeerOptions { Credentials = clientCredentials });
client.Send(new MsmtTarget { Host = "127.0.0.1", Port = 5000 }, "hello"u8.ToArray()); // fire-and-forget

MsmtResponse response = await client.Request(new MsmtTarget { Host = "127.0.0.1", Port = 5000 }, "ping"u8.ToArray());
Console.WriteLine(response.Success);
```

A single `IMsmtMessagePeer` can do both at once: `StartListener` and `Send`/`Request` are independent of each
other, so a peer that also calls `StartListener` can receive messages from targets it never explicitly sent
to, in addition to sending its own. A `Send` that fails has no way to report it unless it is tagged (see
below); use `Request` where the outcome matters.

## Sessions

A session connection is negotiated once and then carries any number of messages, in both directions, until
its lifetime ends or either side closes it. One `IMsmtSessionPeer` type both listens for and opens session
connections; a connection carries no events of its own, so subscribe to the owning peer's `PackageChanged`
and `Disconnected`, each carrying the `IMsmtConnection` it is about, and set its `Receiver` for messages.

```csharp
IMsmtSessionPeer listening = new IMsmtSessionPeer.Factory().Create(new MsmtSessionPeerOptions { Credentials = serverCredentials, RequireFullyQualifiedHostname = false });

// Connected publishes before the connection starts delivering messages, so subscribing here misses nothing.
listening.Connected.Subscribe(connection => connection.Send("welcome"u8.ToArray())); // the listening side can send too
listening.Receiver = (connection, payload, responder) =>
{
    payload.Dispose();
    responder?.Accept("got it"u8.ToArray());
};
listening.Disconnected.Subscribe(args => Console.WriteLine($"{args.Connection.Remote.Host} closed: {args.Exception?.Message ?? "normally"}"));
listening.StartListener(port: 5000);

IMsmtSessionPeer connecting = new IMsmtSessionPeer.Factory().Create(new MsmtSessionPeerOptions { Credentials = clientCredentials });
connecting.Receiver = (connection, payload, responder) =>
{
    Console.WriteLine("listening side says something");
    payload.Dispose();
    responder?.Accept();
};

// Connect returns immediately, still MsmtConnectionStatus.Connecting: the TCP connect, TLS handshake, and
// session negotiation all happen in the background.
IMsmtConnection connection = connecting.Connect(new MsmtNameTarget { Host = "127.0.0.1", Port = 5000, ServerName = "127.0.0.1" });
bool connected = await connection.Wait(); // true once Connected, false if it went straight to Disconnected
MsmtResponse response = await connection.Request("hello"u8.ToArray());
```

Both sides hold an `IMsmtConnection` and use it the same way: `Send`/`Request` to the other side, and the
owning peer's `Receiver` for what the other side sends. At most one message per direction is in flight at
once, but the two directions do not wait for each other, so both sides may send at the same time. Every
`Connect` opens a new connection; keeping or reusing them is up to the caller.

A `Receiver` set only after `Connect` returns may miss a message the listening side sent immediately, so
prefer setting it before calling `Connect`, as above - `Receiver` never depends on knowing which connection
a message will arrive on ahead of time, since it is peer-wide.

```csharp
await connection.DisposeAsync(); // gracefully; connection.Dispose() closes immediately
```

`Disconnected` carries the exception that ended the connection, such as a `TimeoutException`, or `null` when
it closed normally - including a connection that never finished connecting in the first place, such as one
whose remote peer never answered, so there is no separate failed-to-connect event to watch in addition to
it. `IMsmtSessionPeer.Connections` snapshots what is open, including a connection still `Connecting`.

## Deciding a response later

```csharp
IMsmtResponder? pending = null;

peer.Receiver = (source, identity, payload, responder) => // or sessionPeer.Receiver
{
    payload.Dispose(); // no need to hold the payload while waiting
    pending = responder; // null unless the sender used Request
};

// ...elsewhere, once ready to decide - from any thread, inside the receiver or long after it returned:
pending?.Accept("done"u8.ToArray());
```

A receiver is a plain synchronous callback and its returning means nothing about whether a message was
answered, so deciding later is just keeping the responder and calling `Accept` or `Reject` on it, once,
whenever the decision is made. Nothing checks that a requested message is ever answered: the sender's
`ResponseTimeout` is what ends the wait for one that isn't, and the connection stays open until then, so
an answer that does come in time still reaches the sender. A second answer throws
`InvalidOperationException`. Messages on a connection are handed to the receiver one at a time, so one that
blocks holds up the ones behind it, and anything slow is better handed off and answered afterward. On a
session connection the connection keeps reading meanwhile, so a message this side sent is still
acknowledged. If the peer shuts down first, the connection closes without acknowledging the message, and
the sender's `Request` fails.

## Reporting exceptions

```csharp
peer.Exceptions.Subscribe(exception => Console.Error.WriteLine(exception)); // sessionPeer.Exceptions works the same way
```

`Exceptions` on `IMsmtMessagePeer` and `IMsmtSessionPeer` publishes what goes wrong while processing
something without breaking a connection, so it would otherwise disappear: a `Receiver` that throws, a
subscriber to `PackageChanged` or `Disconnected` that throws, a connection accepted by a message peer's
listener that failed to establish, a failure evicting a connection, and the listener failing to accept a
client. A receiver that throws does not close its connection or stop later messages, and its message, if it
requested an acknowledgement, is simply left unanswered. An exception that ends a connection is reported
through that connection's `Disconnected` instead, and a `Connected` subscriber that throws still closes the
connection it was given. `Exceptions` publishes synchronously on whichever thread met the exception, and a
subscriber to it that throws is ignored.

## Sending with pooled memory

`Send`/`Request` (and `IMsmtResponder.Accept`/`.Reject`), on peers and connections alike, each have two
overloads: one taking a `ReadOnlyMemory<byte>` (wrapped without copying, so it must not be mutated until the
send completes; used above), and one taking ownership of an `IMemoryOwner<byte>`, avoiding an allocation per
message for callers already using pooled buffers. The whole of the owner's `Memory` is sent, and
`MemoryPool<byte>.Shared.Rent` may return a larger buffer than requested, so trim it to the payload's
exact length with `Slice(start, length)`:

```csharp
IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(payload.Length).Slice(0, payload.Length);
payload.CopyTo(owner.Memory.Span);

// The peer disposes `owner` once the send completes, successfully or not - do not use or dispose it
// yourself afterward. If this call throws instead, ownership stays with the caller.
peer.Send(target, owner);
```

`Slice` returns a new owner over the same buffer; disposing it still returns the whole rented buffer to
its pool, and the owner it was called on must not be disposed separately.

A response's payload (`MsmtResponse.Payload`, returned by `Request`) is likewise a pooled
`IMemoryOwner<byte>` whose ownership transfers to the caller, who should dispose it once done. An
acknowledgement payload passed to `IMsmtResponder.Accept`/`.Reject` transfers ownership the same way,
disposed once sent. A `Receiver`'s incoming payload is likewise an `IMemoryOwner<byte>` whose ownership
transfers to the receiver, which must dispose it, and can do so as soon as it has read what it needs -
before answering, if it has to wait on something else first.

## Tracking a send with a tag

```csharp
object tag = new();
peer.Send(target, payload, new MsmtSendOptions { Tag = tag, Priority = 5 });

peer.PackageChanged.Subscribe(args => // sessionPeer.PackageChanged works the same way, and also carries args.Connection
{
    if (args.Package.Tag == tag)
    {
        Console.WriteLine($"Send status: {args.Status} {args.Exception?.Message}");
    }
});

IMsmtPackage? package = peer.GetPackage(tag); // null if no send was ever queued with this tag, or it was long forgotten
package?.Cancel(); // best-effort: cancels immediately if still queued, or closes the connection if already in flight

IReadOnlyList<IMsmtPackage> active = peer.Packages; // every currently active (not yet finished) package
```

`MsmtPackageChange.Status` is fixed at the moment this event was raised. `args.Package.Status`
instead always reflects the package's *current* status, which may have already moved on by the time a
subscriber gets to it, so prefer `args.Status` when reacting to this specific transition. A send ends
`Completed`, `Cancelled`, or `Failed`, and a failure carries its exception in `args.Exception`.

An untagged send (`Tag = null`, the default) never publishes to `PackageChanged` and has no
`IMsmtPackage` to look up or cancel. A tagged send is tracked by the peer itself - `Packages`/`GetPackage`
are declared on `IMsmtPeer` and work the same way on `IMsmtMessagePeer` and `IMsmtSessionPeer` alike - so
its package stays available even after the pooled sender or the connection that carried it is gone; a
connection itself tracks no packages. A finished send's package is forgotten five minutes after it
finished, and a tag identifies a send within its peer.

## Marking QoS with DSCP

Per the ICD, a send may mark its packets with a DSCP (Differentiated Services Code Point) for
network-level quality of service - a 6-bit value from 0 to 63 (e.g. 46 for the standard Expedited
Forwarding class); assigning anything outside that range throws `ArgumentOutOfRangeException`:

```csharp
peer.Send(target, payload, new MsmtSendOptions { Dscp = 46 });
```

The mark is applied to the underlying TCP socket immediately before that payload is written, not once per
connection - so a connection reused across sends is re-marked for each one, even if a later send requests a
different value than an earlier one on the same connection. Marking is best-effort: the ICD does not
mandate any particular value or guarantee that a network - or even the local platform - honors it, and a
platform that rejects the marking does not fail the send. Omit `Dscp` (the default) to leave the socket
unmarked.

## Reachability check

`IMsmtReachabilityChecker.Reach` establishes a connection and exchanges a specially flagged message that the
remote peer's listener echoes back without ever invoking its `Receiver` - a "ping" that verifies a peer is
reachable and correctly configured without generating real message traffic or invoking application logic:

```csharp
IMsmtReachabilityChecker checker = new MsmtReachabilityChecker();
bool reachable = await checker.Reach(new MsmtNameTarget { Host = "127.0.0.1", Port = 5000, ServerName = "127.0.0.1" }, new MsmtOptions { Credentials = credentials });
```

`Reach` takes its options directly, one call at a time, since a checker carries no configuration of its own
and needs none registered for dependency injection beyond itself.

## Limits, timeouts and keep-alives

```csharp
MsmtSessionPeerOptions options = new()
{
    Credentials = credentials,
    HandshakeTimeout = TimeSpan.FromSeconds(30), // TCP connect + TLS handshake (and a listener's wait for a first message)
    StallTimeout = TimeSpan.FromSeconds(30),     // no bytes moving while transferring a message
    ResponseTimeout = TimeSpan.FromMinutes(2),   // waiting for the remote application's acknowledgement
    TcpKeepAliveTime = TimeSpan.FromSeconds(60), // OS-level probing of a silent connection
};
```

Peers, session peers and reachability checkers share these options through `MsmtOptions`, and each
can be `null` to disable it. A phase that runs out of time drops the connection and surfaces as a
`TimeoutException`. `ResponseTimeout` is the one to raise if a subscriber legitimately defers its response
for longer than the default. A cancelled `CancellationToken` still surfaces as `OperationCanceledException`.

```csharp
MsmtMessagePeerOptions peerOptions = new()
{
    Credentials = credentials,
    MaxIdleTime = TimeSpan.FromMinutes(5), // disconnect a connection with no application traffic for this long
    MaxConnectionCount = 100,              // beyond this, disconnect the least recently used idle ones
};
```

Only `IMsmtMessagePeer` disconnects connections on its own; `MaxIdleTime` and `MaxConnectionCount` are
`MsmtMessagePeerOptions` only. `MaxIdleTime` counts application traffic in either direction, so a target kept alive
by keep-alives is still discarded once nothing is sent on it, along with its per-target sender, even in
Message mode; a new one is created transparently by the next send. `IMsmtSessionPeer` never disconnects a
session on its own for being idle or unused - only a timeout, a stall, or the caller explicitly disposing
one closes it, since managing session connections is entirely up to the caller.

In a session the initiating side sends a keep-alive after a randomized `KeepAliveMinInterval` to
`KeepAliveMaxInterval` (3 to 5 minutes by default, per the ICD) without traffic, and both sides close the
connection when its negotiated lifetime ends (`MsmtSessionPeerOptions.SessionLifetime`, capped by the
accepting side's `MsmtSessionPeerOptions.MaximumSessionLifetime`). `MsmtMessagePeerOptions.RekeyLimit` (above `1`)
is enforced by both sides for Message mode with rekeying.

## Disposal

`IMsmtMessagePeer` and `IMsmtSessionPeer` implement both `IDisposable` and `IAsyncDisposable`, as does
`IMsmtConnection`: `Dispose()` immediately stops accepting new connections (or, on a connection, closes it)
without waiting for background work to finish; `DisposeAsync()` waits for that work to fully stop first,
for a graceful shutdown. Either may be called more than once; afterward, calls that would start something
throw `ObjectDisposedException`:

```csharp
await using IMsmtMessagePeer peer = peerFactory.Create(options);
// ...
```

## Dependency injection

`AddMsmt()` registers `IMsmtMessagePeer.IFactory`, `IMsmtSessionPeer.IFactory` and `IMsmtReachabilityChecker`
into an `IServiceCollection`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using BlueHeighliner.Msmt;

ServiceCollection services = new();
services.AddMsmt();
ServiceProvider provider = services.BuildServiceProvider();

IMsmtMessagePeer.IFactory peerFactory = provider.GetRequiredService<IMsmtMessagePeer.IFactory>();
```

Without an IoC container, `new IMsmtMessagePeer.Factory()`, `new IMsmtSessionPeer.Factory()` and `new
MsmtReachabilityChecker()` (no arguments) build the equivalent instances directly.
