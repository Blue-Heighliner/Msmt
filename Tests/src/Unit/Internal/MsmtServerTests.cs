namespace BlueHeighliner.Msmt.Tests.Unit.Internal;

/// <summary>Unit tests for <see cref="MsmtServer"/> that don't require a live connection.</summary>
public sealed class MsmtServerTests
{
    /// <summary>Before <see cref="MsmtServer.Host"/> is called, the server has no local endpoint.</summary>
    [Fact]
    public async Task LocalEndPoint_BeforeHost_IsNull()
    {
        await using MsmtServer server = new();

        Assert.Null(server.LocalEndPoint);
    }

    /// <summary><see cref="MsmtServer.Dispose"/> immediately stops accepting connections without waiting for any to finish.</summary>
    [Fact]
    public void Dispose_ImmediatelyStopsAccepting()
    {
        (X509Certificate2 certificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        MsmtServer server = new();
        server.Host(new MsmtHostOptions
        {
            Host = "127.0.0.1",
            Port = 0,
            Credentials = new MsmtCredentials { Identity = certificate, TrustedAuthorities = trustedAuthorities },
        });

        server.Dispose();
    }

    /// <summary><see cref="MsmtServer.Dispose"/> does nothing if <see cref="MsmtServer.Host"/> was never called.</summary>
    [Fact]
    public void Dispose_NeverHosted_DoesNothing()
    {
        MsmtServer server = new();

        server.Dispose();
    }
}
