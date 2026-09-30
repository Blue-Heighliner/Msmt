namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// A minimal hot <see cref="IObservable{T}"/> that synchronously multicasts each published value to every
/// currently subscribed observer, with no buffering or replay for a late subscriber - equivalent to a
/// plain multicast event, but exposed as an observable.
/// </summary>
/// <param name="onObserverError">
/// Given an exception an observer throws while being published to, after which the remaining observers are
/// still published to; <see langword="null"/> to let it propagate to the publisher instead.
/// </param>
internal sealed class MsmtEventSubject<T>(Action<Exception>? onObserverError = null) : IObservable<T>
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
            try
            {
                observer.OnNext(value);
            }
            catch (Exception exception) when (onObserverError is not null)
            {
                onObserverError(exception);
            }
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
        /// <inheritdoc />
        public void Dispose() => subject.Remove(observer);
    }
}
