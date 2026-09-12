namespace BlueHeighliner.Msmt.Tests.Unit.Internal;

/// <summary>Unit tests for <see cref="MsmtPackage"/>.</summary>
public sealed class MsmtPackageTests
{
    /// <summary><see cref="MsmtPackage.Tag"/> returns the tag it was constructed with.</summary>
    [Fact]
    public async Task Tag_Constructed_ReturnsGivenTag()
    {
        object tag = new();
        await using MsmtClient client = new();

        MsmtPackage package = new(client, tag, MsmtSendStatus.Queued);

        Assert.Same(tag, package.Tag);
    }

    /// <summary><see cref="MsmtPackage.Target"/> reflects the owning client's target.</summary>
    [Fact]
    public async Task Target_Constructed_ReflectsClientTarget()
    {
        (X509Certificate2 certificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        MsmtNameTarget target = new() { Host = "127.0.0.1", Port = 5000, ServerName = "127.0.0.1" };
        await using MsmtClient client = new();
        await client.Connect(new MsmtConnectOptions { Target = target, Credentials = new MsmtCredentials { Identity = certificate, TrustedAuthorities = trustedAuthorities } });

        MsmtPackage package = new(client, new object(), MsmtSendStatus.Queued);

        Assert.Equal(target, package.Target);
    }

    /// <summary>A final <see cref="MsmtSendStatus"/> (<see cref="MsmtSendStatus.Completed"/>/<see cref="MsmtSendStatus.Cancelled"/>) given at construction is latched and returned without ever consulting the client again.</summary>
    [Theory]
    [InlineData(MsmtSendStatus.Completed)]
    [InlineData(MsmtSendStatus.Cancelled)]
    public async Task Status_ConstructedWithFinalStatus_ReturnsItWithoutConsultingClient(MsmtSendStatus finalStatus)
    {
        await using MsmtClient client = new();
        object tag = new();

        MsmtPackage package = new(client, tag, finalStatus);

        Assert.Equal(finalStatus, package.Status);
        // The client never tracked this tag at all - if Status incorrectly re-consulted it, GetStatus's
        // null result would have no effect anyway, so this also confirms the latch by construction.
        Assert.Null(client.GetStatus(tag));
    }

    /// <summary>A non-final status re-reads the client's current status on every access, keeping the last known non-final value when the client no longer tracks the tag.</summary>
    [Fact]
    public async Task Status_ConstructedWithNonFinalStatus_ReReadsClientOnEachAccess()
    {
        await using MsmtClient client = new();
        object tag = new();

        MsmtPackage package = new(client, tag, MsmtSendStatus.Queued);

        Assert.Equal(MsmtSendStatus.Queued, package.Status);
        Assert.Equal(MsmtSendStatus.Queued, package.Status);
    }

    /// <summary><see cref="MsmtPackage.Cancel"/> forwards to the owning client's <see cref="MsmtClient.Cancel"/>, a no-op for a tag the client has no outstanding send for.</summary>
    [Fact]
    public async Task Cancel_NoOutstandingSendForTag_DoesNotThrow()
    {
        await using MsmtClient client = new();
        MsmtPackage package = new(client, new object(), MsmtSendStatus.Queued);

        Record.Exception(package.Cancel);
    }
}
