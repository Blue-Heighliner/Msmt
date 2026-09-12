namespace BlueHeighliner.Msmt.Tests.Unit.Internal;

/// <summary>Unit tests for <see cref="MsmtEventSubject{T}"/> and <see cref="MsmtObservableExtensions"/>.</summary>
public sealed class MsmtEventSubjectTests
{
    /// <summary>Publishing with no subscribers is a no-op.</summary>
    [Fact]
    public void Publish_NoSubscribers_DoesNothing()
    {
        MsmtEventSubject<int> subject = new();

        Record.Exception(() => subject.Publish(1));
    }

    /// <summary>A subscriber added via <see cref="IObservable{T}.Subscribe"/> receives every subsequently published value.</summary>
    [Fact]
    public void Subscribe_ThenPublish_ObserverReceivesValue()
    {
        MsmtEventSubject<int> subject = new();
        List<int> received = [];
        RecordingObserver<int> observer = new(received);

        subject.Subscribe(observer);
        subject.Publish(1);
        subject.Publish(2);

        Assert.Equal([1, 2], received);
    }

    /// <summary>Every currently subscribed observer receives a published value, in subscription order.</summary>
    [Fact]
    public void Publish_MultipleSubscribers_AllReceiveInOrder()
    {
        MsmtEventSubject<int> subject = new();
        List<int> firstReceived = [];
        List<int> secondReceived = [];

        subject.Subscribe(new RecordingObserver<int>(firstReceived));
        subject.Subscribe(new RecordingObserver<int>(secondReceived));
        subject.Publish(42);

        Assert.Equal([42], firstReceived);
        Assert.Equal([42], secondReceived);
    }

    /// <summary>Disposing the token returned by <see cref="IObservable{T}.Subscribe"/> stops further values from reaching that observer.</summary>
    [Fact]
    public void Dispose_Subscription_StopsReceivingFurtherValues()
    {
        MsmtEventSubject<int> subject = new();
        List<int> received = [];
        IDisposable subscription = subject.Subscribe(new RecordingObserver<int>(received));

        subject.Publish(1);
        subscription.Dispose();
        subject.Publish(2);

        Assert.Equal([1], received);
    }

    /// <summary><see cref="MsmtObservableExtensions.Subscribe{T}"/> invokes the given delegate for each published value without requiring an <see cref="IObserver{T}"/> implementation.</summary>
    [Fact]
    public void SubscribeExtension_Published_InvokesDelegate()
    {
        MsmtEventSubject<int> subject = new();
        List<int> received = [];

        subject.Subscribe(received.Add);
        subject.Publish(7);

        Assert.Equal([7], received);
    }

    /// <summary>Disposing the token returned by the <see cref="MsmtObservableExtensions.Subscribe{T}"/> extension stops further values from reaching the delegate.</summary>
    [Fact]
    public void SubscribeExtension_Disposed_StopsReceivingFurtherValues()
    {
        MsmtEventSubject<int> subject = new();
        List<int> received = [];

        IDisposable subscription = subject.Subscribe(received.Add);
        subject.Publish(1);
        subscription.Dispose();
        subject.Publish(2);

        Assert.Equal([1], received);
    }

    /// <summary>
    /// <see cref="MsmtObservableExtensions.Subscribe{T}"/>'s wrapped observer's <see
    /// cref="IObserver{T}.OnError"/>/<see cref="IObserver{T}.OnCompleted"/> are both no-ops that don't
    /// throw - <see cref="MsmtEventSubject{T}"/> itself never calls either (only <see
    /// cref="IObserver{T}.OnNext"/>), but the wrapper must still tolerate an <see cref="IObservable{T}"/>
    /// that does.
    /// </summary>
    [Fact]
    public void SubscribeExtension_OnErrorAndOnCompleted_AreNoOps()
    {
        CapturingObservable<int> observable = new();

        observable.Subscribe(_ => { });

        Record.Exception(() => observable.CapturedObserver!.OnError(new InvalidOperationException()));
        Record.Exception(observable.CapturedObserver!.OnCompleted);
    }

    private sealed class CapturingObservable<T> : IObservable<T>
    {
        public IObserver<T>? CapturedObserver { get; private set; }

        public IDisposable Subscribe(IObserver<T> observer)
        {
            CapturedObserver = observer;
            return new NoOpDisposable();
        }

        private sealed class NoOpDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class RecordingObserver<T>(List<T> received) : IObserver<T>
    {
        public void OnNext(T value) => received.Add(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
