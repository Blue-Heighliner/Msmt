# Usage

Runnable examples of `IMsmtPeer` in different situations. See [Api.md](Api.md) for the design and flow
behind these calls, and [Architecture.md](Architecture.md) for why the API is shaped this way.

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
IMsmtPeer peer = new MsmtPeerFactory().Create(new MsmtOptions { Credentials = credentials, RequireFullyQualifiedHostname = false });

peer.Received.Subscribe(args =>
{
    using (args.Payload)
    {
        Console.WriteLine(Encoding.UTF8.GetString(args.Payload.Memory.Span));
    }
});

peer.StartListener(port: 5000);
peer.Send(new MsmtTarget { Host = "127.0.0.1", Port = 5000 }, "hello"u8.ToArray());
```

## Client/server request-response

```csharp
using BlueHeighliner.Msmt;

MsmtCredentials serverCredentials = MsmtCredentials.FromPemFiles("server.pem", "server.key", "ca.pem");
MsmtCredentials clientCredentials = MsmtCredentials.FromPemFiles("client.pem", "client.key", "ca.pem");

// --- Server side ---
IMsmtPeerFactory peerFactory = new MsmtPeerFactory();

// RequireFullyQualifiedHostname is disabled here only because this example connects by loopback IP
// address rather than a real DNS hostname; leave it enabled (the default) whenever a hostname is used.
IMsmtPeer server = peerFactory.Create(new MsmtOptions { Credentials = serverCredentials, RequireFullyQualifiedHostname = false });

server.Received.Subscribe(args =>
{
    using (args.Payload)
    {
        Console.WriteLine(Encoding.UTF8.GetString(args.Payload.Memory.Span));
    }

    // Only meaningful if args.IsResponseRequested is true (i.e. the sender used Request, not Send) -
    // calling Accept/Reject otherwise throws. If a response was requested but no subscriber decides,
    // it is accepted automatically once every subscriber has run.
    if (args.IsResponseRequested)
    {
        args.Responder.Accept(); // or args.Responder.Reject() to negatively acknowledge it.
    }
});

server.StartListener(port: 5000);

// --- Client side ---
IMsmtPeer client = peerFactory.Create(new MsmtOptions { Credentials = clientCredentials });
client.Send(new MsmtTarget { Host = "127.0.0.1", Port = 5000 }, "hello"u8.ToArray()); // fire-and-forget

MsmtResponse response = await client.Request(new MsmtTarget { Host = "127.0.0.1", Port = 5000 }, "ping"u8.ToArray());
Console.WriteLine(response.Success);
```

A single `IMsmtPeer` can act as both sides at once: `StartListener` and `Send`/`Request` are independent
of each other, so a peer that also calls `StartListener` can receive messages from targets it never
explicitly sent to, in addition to sending its own.

## Observing link and connection events

```csharp
peer.Linking.Subscribe(args => Console.WriteLine($"Connecting to/from {args.Link.Connection.Target}..."));
peer.Linked.Subscribe(args => Console.WriteLine($"Linked ({args.Link.Kind}): {args.Link.Identity?.Subject}"));
peer.LinkFailed.Subscribe(args => Console.WriteLine($"Link failed: {args.Exception.Message}"));
peer.Unlinked.Subscribe(args => Console.WriteLine($"Unlinked ({args.Link.Kind}): {args.Exception?.Message}"));
peer.Connected.Subscribe(args => Console.WriteLine($"Connected: {args.Connection.Target}"));
peer.Disconnected.Subscribe(args => Console.WriteLine($"Disconnected: {args.Connection.Target}"));
```

Each is a plain, hot `IObservable<T>`: subscribing only receives values published after `Subscribe` is
called (no replay of past ones), and publishing multicasts synchronously to every subscriber currently
attached, in subscription order - equivalent to a C# multicast event. `MsmtObservableExtensions.Subscribe`
lets a plain `Action<T>` subscribe without implementing `IObserver<T>`, as used throughout this document;
dispose the returned `IDisposable` to unsubscribe.

`Linking`/`Linked`/`LinkFailed`/`Unlinked` fire per link (once per direction); `Connected`/`Disconnected`
instead summarize a whole `IMsmtConnection`, firing once while it holds at least one link and once again
when it holds none. A connection with only one direction ever open - e.g. a Message Mode sender, which
closes its only link after every message - sees the two pairs fire in lockstep, once per message.

## Deferring a response

```csharp
IMsmtResponder? pending;

server.Received.Subscribe(args =>
{
    if (args.IsResponseRequested)
    {
        pending = args.Responder;
        args.Responder.Defer(); // decide later, from outside this subscriber
    }
});

// ...elsewhere, once ready to decide:
pending?.Accept();
```

`Defer()` suppresses the automatic acceptance that would otherwise happen once every subscriber has run,
so something else — a background task, a queue, another connection's handler — can call `Accept`/`Reject`
at some later point instead. Since MSMT never has more than one message in flight per connection, that
connection reads no further message until the responder is decided, so hold onto a deferred
`IMsmtResponder` only as long as actually needed.

## Sending with pooled memory

`IMsmtPeer.Send`/`.Request` (and `IMsmtResponder.Accept`/`.Reject`) each have two overloads: one taking a
`ReadOnlyMemory<byte>` (copied into a non-pooled wrapper, used above), and one taking ownership of an
`IMemoryOwner<byte>` — e.g. rented from `MemoryPool<byte>.Shared` — avoiding a copy for callers already
using pooled buffers:

```csharp
IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(payload.Length);
payload.CopyTo(owner.Memory.Span);

// The peer disposes `owner` once the send completes, successfully or not - do not use or dispose it
// yourself afterward. If this call throws instead, ownership stays with the caller.
peer.Send(target, owner);
```

`Received`'s payload (`MsmtReceivedEventArgs.Payload`) and a response's payload (`MsmtResponse.Payload`,
returned by `Request`) are likewise pooled `IMemoryOwner<byte>` values whose ownership transfers to
whoever handles them, who should dispose them once done (see the `using` in the examples above). An
acknowledgement payload passed to `Accept`/`Reject` transfers ownership the same way, to the
`IMsmtResponder`, which disposes it once sent.

## Tracking a send with a tag

```csharp
object tag = new();
peer.Send(target, payload, new MsmtSendOptions { Tag = tag, Priority = 5 });

peer.PackageChanged.Subscribe(args =>
{
    if (args.Package.Tag == tag)
    {
        Console.WriteLine($"Send status: {args.Status}");
    }
});

IMsmtPackage? package = peer.GetPackage(tag); // null once no send was ever queued with this tag
package?.Cancel(); // best-effort: cancels immediately if still queued, or force-closes the connection if already in flight

IReadOnlyList<IMsmtPackage> active = peer.Packages; // every currently active (not yet finished) package
```

`MsmtPackageChangedEventArgs.Status` is fixed at the moment this event was raised. `args.Package.Status`
instead always reflects the package's *current* status, which may have already moved on by the time a
subscriber gets to it — e.g. because an earlier subscriber ran slowly, or the send kept progressing
concurrently on another thread — so prefer `args.Status` when reacting to this specific transition.

An untagged send (`Tag = null`, the default) never publishes to `PackageChanged` and has no
`IMsmtPackage` to look up via `GetPackage` or cancel.

## Marking QoS with DSCP

Per the ICD, a send may mark its packets with a DSCP (Differentiated Services Code Point) for
network-level quality of service — a 6-bit value from 0 to 63 (e.g. 46 for the standard Expedited
Forwarding class); assigning anything outside that range throws `ArgumentOutOfRangeException`:

```csharp
peer.Send(target, payload, new MsmtSendOptions { Dscp = 46 });
```

The mark is applied to the underlying TCP socket immediately before that payload is written, not once
per connection — so a `Session`/`MessageWithRekeying` connection cached and reused across sends is
re-marked for each one, even if a later send requests a different value than an earlier one on the same
connection. Marking is best-effort: the ICD does not mandate any particular value or guarantee that a
network — or even the local platform — honors it, and a platform that rejects the marking does not fail
the send. Omit `Dscp` (the default) to leave the socket unmarked.

## Reachability check

`Test` establishes a connection and exchanges a specially flagged message that the remote peer's
listener echoes back without ever publishing to `Received` — a "ping" that verifies a peer is reachable
and correctly configured without generating real message traffic or invoking application logic:

```csharp
bool reachable = await peer.Test(new MsmtTarget { Host = "127.0.0.1", Port = 5000 });
```

## Managing connections

```csharp
IReadOnlyList<IMsmtConnection> active = peer.ActiveConnections; // published Connected, not yet Disconnected

IMsmtConnection? connection = peer.GetActiveConnection(new MsmtTarget { Host = "127.0.0.1", Port = 5000 });
connection?.Drop(); // immediately closes and discards both Sender and Receiver, if open
await (connection?.Disconnect() ?? Task.CompletedTask); // gracefully closes both, awaiting each

connection?.Sender?.Drop(); // immediately closes and discards just the sender
await (connection?.Receiver?.Disconnect() ?? Task.CompletedTask); // gracefully closes just the receiver
```

`ActiveConnections` is updated before `Connected` is published for the connection, so it already reflects
it when a subscriber runs.

`GetActiveConnection` matches by address and port alone (ignoring server name), but only returns a result
while the connection holds exactly one linked link: its `Sender` if this peer's own outgoing link to that
target is the linked one, or its `Receiver` if an incoming link its listener separately accepted from that
same address and port is instead. Returns `null` if no connection for the target exists, or one does but
currently has neither link linked yet or both linked at once.

`MsmtOptions.MaxIdleTime`/`MaxConnectionCount` also evict connections automatically - both the on-demand
ones a peer creates and the ones its listener accepts - see [Architecture.md](Architecture.md) and
[Implementation.md#eviction](Implementation.md#eviction).

## Connection lifecycle modes

```csharp
MsmtOptions options = new()
{
    Credentials = credentials,
    Mode = MsmtOperationMode.Session,
    SessionLifetime = TimeSpan.FromMinutes(5), // proposed lifetime for connections this peer creates on demand
};
```

`SupportsSessionMode`/`MaximumSessionLifetime` instead govern this peer's *listener* — whether it accepts
a Session Mode negotiation request at all, and the cap it applies to what a connecting client proposes.
See [Architecture.md#three-connection-lifecycle-modes-trading-security-for-overhead](Architecture.md#three-connection-lifecycle-modes-trading-security-for-overhead) for why each
mode exists, and [Implementation.md#connection-lifecycle-modes](Implementation.md#connection-lifecycle-modes)
for how each is implemented.

## Disposal

`IMsmtPeer` implements both `IDisposable` and `IAsyncDisposable`: `Dispose()` immediately stops
accepting new connections and closes every open connection — both accepted and on-demand — without
waiting for background work to finish; `DisposeAsync()` waits for that work to fully stop first, for a
graceful shutdown:

```csharp
await using IMsmtPeer peer = peerFactory.Create(options);
// ...
```

## Dependency injection

`AddMsmt()` registers `IMsmtPeerFactory` into an `IServiceCollection`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using BlueHeighliner.Msmt;

ServiceCollection services = new();
services.AddMsmt();
ServiceProvider provider = services.BuildServiceProvider();

IMsmtPeerFactory peerFactory = provider.GetRequiredService<IMsmtPeerFactory>();
```

Without an IoC container, `new MsmtPeerFactory()` (no arguments) builds an equivalent factory directly.
