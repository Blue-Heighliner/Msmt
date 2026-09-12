namespace BlueHeighliner.Msmt.Tests.Unit.Internal;

/// <summary>Unit tests for <see cref="MsmtConnection"/>.</summary>
public sealed class MsmtConnectionTests
{
    /// <summary>A freshly constructed connection has neither a sender nor a receiver, and is therefore empty.</summary>
    [Fact]
    public void IsEmpty_FreshlyConstructed_IsTrue()
    {
        MsmtConnection connection = new(new MsmtTarget { Host = "127.0.0.1", Port = 5000 });

        Assert.True(connection.IsEmpty);
        Assert.Null(connection.SenderEngine);
        Assert.Null(connection.ReceiverEngine);
    }

    /// <summary><see cref="MsmtConnection.EnsureSender"/> invokes the factory exactly once and returns the same engine on every subsequent call.</summary>
    [Fact]
    public void EnsureSender_CalledRepeatedly_InvokesFactoryOnceAndReturnsSameEngine()
    {
        MsmtConnection connection = new(new MsmtTarget { Host = "127.0.0.1", Port = 5000 });
        int factoryCalls = 0;
        MsmtClient Factory()
        {
            Interlocked.Increment(ref factoryCalls);
            return new MsmtClient();
        }

        MsmtClient first = connection.EnsureSender(Factory);
        MsmtClient second = connection.EnsureSender(Factory);

        Assert.Same(first, second);
        Assert.Equal(1, factoryCalls);
        Assert.Same(first, connection.SenderEngine);
        Assert.False(connection.IsEmpty);
    }

    /// <summary><see cref="MsmtConnection.EnsureSender"/> races many concurrent callers to the same engine, invoking the factory exactly once.</summary>
    [Fact]
    public async Task EnsureSender_ConcurrentCallers_AllReceiveSameEngineFromSingleFactoryInvocation()
    {
        MsmtConnection connection = new(new MsmtTarget { Host = "127.0.0.1", Port = 5000 });
        int factoryCalls = 0;
        MsmtClient Factory()
        {
            Interlocked.Increment(ref factoryCalls);
            return new MsmtClient();
        }

        MsmtClient[] results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => connection.EnsureSender(Factory))));

        Assert.Equal(1, factoryCalls);
        Assert.All(results, client => Assert.Same(results[0], client));
    }

    /// <summary><see cref="MsmtConnection.AttachReceiver"/> attaches the link and points it back at this connection.</summary>
    [Fact]
    public void AttachReceiver_Called_AttachesLinkAndSetsItsConnectionBackReference()
    {
        MsmtTarget target = new() { Host = "127.0.0.1", Port = 5000 };
        MsmtConnection connection = new(target);
        MsmtServerConnection link = new(target);

        connection.AttachReceiver(link);

        Assert.Same(link, connection.ReceiverEngine);
        Assert.Same(connection, link.Connection);
        Assert.False(connection.IsEmpty);
    }

    /// <summary><see cref="MsmtConnection.RemoveReceiver"/> detaches the given link only if it is still the currently attached one.</summary>
    [Fact]
    public void RemoveReceiver_DifferentLinkThanAttached_LeavesAttachedLinkUnaffected()
    {
        MsmtTarget target = new() { Host = "127.0.0.1", Port = 5000 };
        MsmtConnection connection = new(target);
        MsmtServerConnection attached = new(target);
        MsmtServerConnection other = new(target);
        connection.AttachReceiver(attached);

        connection.RemoveReceiver(other);
        Assert.Same(attached, connection.ReceiverEngine);

        connection.RemoveReceiver(attached);
        Assert.Null(connection.ReceiverEngine);
        Assert.True(connection.IsEmpty);
    }

    /// <summary><see cref="MsmtConnection.RemoveSender"/> detaches the given engine only if it is still the currently attached one.</summary>
    [Fact]
    public void RemoveSender_DifferentEngineThanAttached_LeavesAttachedEngineUnaffected()
    {
        MsmtConnection connection = new(new MsmtTarget { Host = "127.0.0.1", Port = 5000 });
        MsmtClient attached = connection.EnsureSender(() => new MsmtClient());
        MsmtClient other = new();

        connection.RemoveSender(other);
        Assert.Same(attached, connection.SenderEngine);

        connection.RemoveSender(attached);
        Assert.Null(connection.SenderEngine);
        Assert.True(connection.IsEmpty);
    }

    /// <summary>Neither <see cref="IMsmtConnection.Sender"/> nor <see cref="IMsmtConnection.Receiver"/> is non-<see langword="null"/> until the underlying engine actually reports itself connected.</summary>
    [Fact]
    public void SenderAndReceiver_EngineNotConnected_AreNull()
    {
        MsmtTarget target = new() { Host = "127.0.0.1", Port = 5000 };
        MsmtConnection connection = new(target);
        connection.EnsureSender(() => new MsmtClient());
        connection.AttachReceiver(new MsmtServerConnection(target));

        Assert.Null(connection.Sender);
        Assert.Null(connection.Receiver);
        Assert.Null(connection.Identity);
    }
}
