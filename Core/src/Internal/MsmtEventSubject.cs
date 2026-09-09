namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// A minimal hot <see cref="IObservable{T}"/> that synchronously multicasts each published value to every
/// currently subscribed observer, with no buffering or replay for a late subscriber - equivalent to a
/// plain multicast event, but exposed as an observable.
/// </summary>
internal sealed class MsmtEventSubject<T> : IObservable<T>
{
    private readonly Lock subscribersLock = new();
    private readonly List<IObserver<T>> subscribers = [];

    /// <summary>Publishes <paramref name="value"/> to every observer currently subscribed, in subscription order.</summary>
    /// <param name="value">The value to publish.</param>
    public void Publish(T value)
    {
        IObserver<T>[] snapshot;
        lock (subscribersLock)
        {
            snapshot = [.. subscribers];
        }

        foreach (IObserver<T> observer in snapshot)
        {
            observer.OnNext(value);
        }
    }

    /// <inheritdoc />
    public IDisposable Subscribe(IObserver<T> observer)
    {
        lock (subscribersLock)
        {
            subscribers.Add(observer);
        }

        return new Unsubscriber(this, observer);
    }

    private void Remove(IObserver<T> observer)
    {
        lock (subscribersLock)
        {
            subscribers.Remove(observer);
        }
    }

    private sealed class Unsubscriber(MsmtEventSubject<T> subject, IObserver<T> observer) : IDisposable
    {
        public void Dispose() => subject.Remove(observer);
    }
}
