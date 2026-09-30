# Observable plumbing

`MsmtEventSubject<T>` is a minimal hand-rolled hot `IObservable<T>` - no buffering or replay, synchronous
multicast to every current subscriber - backing every public `IObservable<T>` property of
`IMsmtMessagePeer` and `IMsmtSessionPeer`: `PackageChanged` and `Exceptions` on both, and `Connected`/`Disconnected` on the
session peer. A connection holds none of its own: its owner supplies plain callbacks at construction time
instead, which the connection invokes directly, and the owner republishes into its own subjects from there
- so a subject only ever exists at the peer, one per kind of event, shared by every connection that peer
owns.

`Subscribe` adds the observer to an internal list (under a `Lock`) and returns an `Unsubscriber` that
removes it again on `Dispose`; `Publish` takes a snapshot of the current subscriber list and invokes
`OnNext` on each in subscription order, synchronously, on the calling thread - no buffering, no replay for
a late subscriber, and no scheduling of any kind. A subject can also be given an error callback: an observer
that throws is then reported to it instead of propagating to the publisher, and the observers after it
still get the value. The peers give one to `PackageChanged` and `Disconnected`, routing to `Exceptions`, so
a bad subscriber can neither break a send or a disconnection nor starve the others; `Connected` has none,
since a subscriber that throws there deliberately closes the connection it was handed. `Exceptions`
itself has none either, and the peer swallows anything its own subscribers throw, since there is nowhere
left to report it.

Receiving a message is not one of these: exactly one decision is ever needed per message, so `Receiver` is
a plain settable delegate property (`MsmtMessageReceiver`/`MsmtSessionReceiver`) instead of an
`IObservable<T>` - there is nothing to multicast to, and the connection calls it directly, with an `IMsmtResponder` to
acknowledge the message through, rather than publishing to any subject at all.
