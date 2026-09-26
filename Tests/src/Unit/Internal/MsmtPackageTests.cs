namespace BlueHeighliner.Msmt.Tests.Unit.Internal;

/// <summary>Unit tests for <see cref="MsmtPackage"/>.</summary>
public sealed class MsmtPackageTests
{
    private readonly MsmtNameTarget target = new() { Host = "127.0.0.1", Port = 5000, ServerName = "127.0.0.1" };

    /// <summary><see cref="MsmtPackage.Tag"/> and <see cref="MsmtPackage.Target"/> return what it was constructed with.</summary>
    [Fact]
    public void TagAndTarget_Constructed_ReturnGivenValues()
    {
        object tag = new();

        MsmtPackage package = new(new Mock<IMsmtPackageTracker>().Object, tag, target, MsmtSendStatus.Queued);

        Assert.Same(tag, package.Tag);
        Assert.Equal(target, package.Target);
    }

    /// <summary>A final <see cref="MsmtSendStatus"/> given at construction is latched and returned without ever consulting the tracker again.</summary>
    [Theory]
    [InlineData(MsmtSendStatus.Completed)]
    [InlineData(MsmtSendStatus.Cancelled)]
    public void Status_ConstructedWithFinalStatus_ReturnsItWithoutConsultingTracker(MsmtSendStatus finalStatus)
    {
        Mock<IMsmtPackageTracker> tracker = new();
        object tag = new();

        MsmtPackage package = new(tracker.Object, tag, target, finalStatus);

        Assert.Equal(finalStatus, package.Status);
        tracker.Verify(instance => instance.GetStatus(It.IsAny<object>()), Times.Never);
    }

    /// <summary>A non-final status re-reads the tracker's current status on every access, and latches once it becomes final.</summary>
    [Fact]
    public void Status_ConstructedWithNonFinalStatus_ReReadsTrackerUntilFinal()
    {
        Mock<IMsmtPackageTracker> tracker = new();
        object tag = new();
        tracker.SetupSequence(instance => instance.GetStatus(tag))
            .Returns(MsmtSendStatus.Transmitting)
            .Returns(MsmtSendStatus.Completed);

        MsmtPackage package = new(tracker.Object, tag, target, MsmtSendStatus.Queued);

        Assert.Equal(MsmtSendStatus.Transmitting, package.Status);
        Assert.Equal(MsmtSendStatus.Completed, package.Status);
        Assert.Equal(MsmtSendStatus.Completed, package.Status);
        tracker.Verify(instance => instance.GetStatus(tag), Times.Exactly(2));
    }

    /// <summary>A tag the tracker has forgotten keeps the last known non-final status.</summary>
    [Fact]
    public void Status_TrackerForgotTag_KeepsLastKnownStatus()
    {
        Mock<IMsmtPackageTracker> tracker = new();
        tracker.Setup(instance => instance.GetStatus(It.IsAny<object>())).Returns((MsmtSendStatus?)null);

        MsmtPackage package = new(tracker.Object, new object(), target, MsmtSendStatus.Queued);

        Assert.Equal(MsmtSendStatus.Queued, package.Status);
    }

    /// <summary><see cref="MsmtPackage.Cancel"/> forwards to the tracker.</summary>
    [Fact]
    public void Cancel_Called_ForwardsToTracker()
    {
        Mock<IMsmtPackageTracker> tracker = new();
        object tag = new();

        new MsmtPackage(tracker.Object, tag, target, MsmtSendStatus.Queued).Cancel();

        tracker.Verify(instance => instance.Cancel(tag), Times.Once);
    }
}
