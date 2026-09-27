# Observable plumbing

`MsmtEventSubject<T>` is a minimal hand-rolled hot `IObservable<T>` - no buffering or replay, synchronous
multicast to every current subscriber - backing every public `IObservable<T>` property of
`IMsmtMessagePeer` and `IMsmtSessionPeer`: `PackageChanged` on both, and `Connected`/`Disconnected` on the
session peer. A connection holds none of its own: its owner supplies plain callbacks at construction time
instead, which the connection invokes directly, and the owner republishes into its own subjects from there
- so a subject only ever exists at the peer, one per kind of event, shared by every connection that peer
owns.

`Subscribe` adds the observer to an internal list (under a `Lock`) and returns an `Unsubscriber` that
removes it again on `Dispose`; `Publish` takes a snapshot of the current subscriber list and invokes
`OnNext` on each in subscription order, synchronously, on the calling thread - no buffering, no replay for
a late subscriber, and no scheduling of any kind.

Receiving a message is not one of these: exactly one decision is ever needed per message, so `Receiver` is
a plain settable delegate property (`MsmtMessageReceiver`/`MsmtSessionReceiver`) instead of an
`IObservable<T>` - there is nothing to multicast to, and the connection awaits its return value directly to
decide how to acknowledge the message, rather than publishing to any subject at all.
