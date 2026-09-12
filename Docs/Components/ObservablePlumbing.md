# Observable plumbing

`MsmtEventSubject<T>` is a minimal hand-rolled hot `IObservable<T>` - no buffering or replay, synchronous
multicast to every current subscriber - backing each of `MsmtPeer`'s public `IObservable<T>` properties.

`Subscribe` adds the observer to an internal list (under a `Lock`) and returns an `Unsubscriber` that
removes it again on `Dispose`; `Publish` takes a snapshot of the current subscriber list and invokes
`OnNext` on each in subscription order, synchronously, on the calling thread - no buffering, no replay for
a late subscriber, and no scheduling of any kind. This is what makes a `Received` subscriber's synchronous
call to `Responder.Accept()`/`Reject()`/`Defer()` work: the publish call that raised `Received` is still on
the stack, on the same connection's handling task, when the subscriber decides.
