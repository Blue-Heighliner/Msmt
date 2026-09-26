namespace BlueHeighliner.Msmt.Tests.Unit.Internal;

/// <summary>Unit tests for <see cref="MsmtConnection"/>.</summary>
public sealed class MsmtConnectionTests
{
    /// <summary>A freshly constructed connection has neither a sender nor a receiver.</summary>
    [Fact]
    public void Constructor_Called_HasNoEngines()
    {
        MsmtConnection connection = new(new MsmtTarget { Host = "127.0.0.1", Port = 5000 });

        Assert.Null(connection.SenderEngine);
        Assert.Null(connection.ReceiverEngine);
    }

    /// <summary><see cref="MsmtConnection.ReserveSender"/> invokes the factory exactly once and returns the same, reserved engine on every subsequent call.</summary>
    [Fact]
    public void ReserveSender_CalledRepeatedly_InvokesFactoryOnceAndReturnsSameReservedEngine()
    {
        MsmtConnection connection = new(new MsmtTarget { Host = "127.0.0.1", Port = 5000 });
        int factoryCalls = 0;
        MsmtClient Factory()
        {
            Interlocked.Increment(ref factoryCalls);
            return new MsmtClient();
        }

        MsmtClient first = connection.ReserveSender(Factory)!;
        MsmtClient second = connection.ReserveSender(Factory)!;

        Assert.Same(first, second);
        Assert.Equal(1, factoryCalls);
        Assert.Same(first, connection.SenderEngine);
        Assert.Same(connection, first.Connection);
        Assert.False(first.IsIdle);
    }

    /// <summary><see cref="MsmtConnection.ReserveSender"/> races many concurrent callers to the same engine, invoking the factory exactly once.</summary>
    [Fact]
    public async Task ReserveSender_ConcurrentCallers_AllReceiveSameEngineFromSingleFactoryInvocation()
    {
        MsmtConnection connection = new(new MsmtTarget { Host = "127.0.0.1", Port = 5000 });
        int factoryCalls = 0;
        MsmtClient Factory()
        {
            Interlocked.Increment(ref factoryCalls);
            return new MsmtClient();
        }

        MsmtClient?[] results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => connection.ReserveSender(Factory))));

        Assert.Equal(1, factoryCalls);
        Assert.All(results, client => Assert.Same(results[0], client));
    }

    /// <summary><see cref="MsmtConnection.RemoveIdleSender"/> leaves a reserved sender attached, and detaches it once every reservation is released.</summary>
    [Fact]
    public void RemoveIdleSender_ReservedSender_DetachesOnlyOnceReleased()
    {
        MsmtConnection connection = new(new MsmtTarget { Host = "127.0.0.1", Port = 5000 });
        MsmtClient sender = connection.ReserveSender(() => new MsmtClient())!;

        Assert.Null(connection.RemoveIdleSender(TimeSpan.Zero));
        Assert.Same(sender, connection.SenderEngine);

        sender.ReleaseReservation();

        Assert.Same(sender, connection.RemoveIdleSender(TimeSpan.Zero));
        Assert.Null(connection.SenderEngine);
    }

    /// <summary><see cref="MsmtConnection.RemoveIdleSender"/> leaves an idle sender attached until it has gone unused for the requested time.</summary>
    [Fact]
    public void RemoveIdleSender_RecentlyActiveSender_DetachesOnlyOnceIdleLongEnough()
    {
        MsmtConnection connection = new(new MsmtTarget { Host = "127.0.0.1", Port = 5000 });
        MsmtClient sender = connection.ReserveSender(() => new MsmtClient())!;
        sender.ReleaseReservation();

        Assert.Null(connection.RemoveIdleSender(TimeSpan.FromMinutes(1)));
        Assert.Same(sender, connection.SenderEngine);

        Assert.Same(sender, connection.RemoveIdleSender(TimeSpan.Zero));
    }

    /// <summary><see cref="MsmtConnection.TryAttachReceiver"/> attaches the link and points it back at this connection.</summary>
    [Fact]
    public void TryAttachReceiver_Called_AttachesLinkAndSetsItsConnectionBackReference()
    {
        MsmtTarget target = new() { Host = "127.0.0.1", Port = 5000 };
        MsmtConnection connection = new(target);
        MsmtServerConnection link = new(target);

        Assert.True(connection.TryAttachReceiver(link));

        Assert.Same(link, connection.ReceiverEngine);
        Assert.Same(connection, link.Connection);
    }

    /// <summary><see cref="MsmtConnection.RemoveReceiver"/> detaches the given link only if it is still the currently attached one.</summary>
    [Fact]
    public void RemoveReceiver_DifferentLinkThanAttached_LeavesAttachedLinkUnaffected()
    {
        MsmtTarget target = new() { Host = "127.0.0.1", Port = 5000 };
        MsmtConnection connection = new(target);
        MsmtServerConnection attached = new(target);
        MsmtServerConnection other = new(target);
        connection.TryAttachReceiver(attached);

        connection.RemoveReceiver(other);
        Assert.Same(attached, connection.ReceiverEngine);

        connection.RemoveReceiver(attached);
        Assert.Null(connection.ReceiverEngine);
    }

    /// <summary><see cref="MsmtConnection.RemoveSender"/> detaches the given engine only if it is still the currently attached one.</summary>
    [Fact]
    public void RemoveSender_DifferentEngineThanAttached_LeavesAttachedEngineUnaffected()
    {
        MsmtConnection connection = new(new MsmtTarget { Host = "127.0.0.1", Port = 5000 });
        MsmtClient attached = connection.ReserveSender(() => new MsmtClient())!;
        MsmtClient other = new();

        connection.RemoveSender(other);
        Assert.Same(attached, connection.SenderEngine);

        connection.RemoveSender(attached);
        Assert.Null(connection.SenderEngine);
    }

    /// <summary><see cref="MsmtConnection.TryRetire"/> refuses while either engine is attached.</summary>
    [Fact]
    public void TryRetire_EngineAttached_ReturnsFalse()
    {
        MsmtTarget target = new() { Host = "127.0.0.1", Port = 5000 };
        MsmtConnection withSender = new(target);
        MsmtConnection withReceiver = new(target);
        withSender.ReserveSender(() => new MsmtClient());
        withReceiver.TryAttachReceiver(new MsmtServerConnection(target));

        Assert.False(withSender.TryRetire());
        Assert.False(withReceiver.TryRetire());
    }

    /// <summary>Once <see cref="MsmtConnection.TryRetire"/> succeeds, the connection refuses to attach any further engine.</summary>
    [Fact]
    public void TryRetire_Empty_RetiresAndRefusesFurtherAttach()
    {
        MsmtTarget target = new() { Host = "127.0.0.1", Port = 5000 };
        MsmtConnection connection = new(target);
        int factoryCalls = 0;

        Assert.True(connection.TryRetire());

        Assert.Null(connection.ReserveSender(() =>
        {
            factoryCalls++;
            return new MsmtClient();
        }));
        Assert.False(connection.TryAttachReceiver(new MsmtServerConnection(target)));
        Assert.Equal(0, factoryCalls);
        Assert.Null(connection.SenderEngine);
        Assert.Null(connection.ReceiverEngine);
    }

    /// <summary>Neither <see cref="IMsmtConnection.Sender"/> nor <see cref="IMsmtConnection.Receiver"/> is non-<see langword="null"/> until the underlying engine actually reports itself connected.</summary>
    [Fact]
    public void SenderAndReceiver_EngineNotConnected_AreNull()
    {
        MsmtTarget target = new() { Host = "127.0.0.1", Port = 5000 };
        MsmtConnection connection = new(target);
        connection.ReserveSender(() => new MsmtClient());
        connection.TryAttachReceiver(new MsmtServerConnection(target));

        Assert.Null(connection.Sender);
        Assert.Null(connection.Receiver);
        Assert.Null(connection.Identity);
    }
}
