namespace BlueHeighliner.Msmt;

/// <summary>
/// Extension members for <see cref="IObservable{T}"/>.
/// </summary>
public static class MsmtObservableExtensions
{
    extension<T>(IObservable<T> observable)
    {
        /// <summary>
        /// Subscribes <paramref name="onNext"/> to be invoked for every value this observable publishes,
        /// without needing to implement <see cref="IObserver{T}"/> directly.
        /// </summary>
        /// <param name="onNext">Invoked synchronously for each published value.</param>
        /// <returns>An <see cref="IDisposable"/> that unsubscribes <paramref name="onNext"/> when disposed.</returns>
        public IDisposable Subscribe(Action<T> onNext) => observable.Subscribe(new MsmtAnonymousObserver<T>(onNext));
    }

    private sealed class MsmtAnonymousObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
