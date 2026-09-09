namespace BlueHeighliner.Msmt.Tests.Unit;

/// <summary>Unit tests for <see cref="MsmtCredentials"/>.</summary>
public sealed class MsmtCredentialsTests
{
    /// <summary>Loading from PEM text reproduces the original identity certificate and trusted authority.</summary>
    [Fact]
    public void FromPemText_ValidText_LoadsIdentityAndTrustedAuthorities()
    {
        (X509Certificate2 serverCertificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        X509Certificate2 authority = trustedAuthorities[0];
        using RSA privateKey = serverCertificate.GetRSAPrivateKey()!;

        MsmtCredentials credentials = MsmtCredentials.FromPemText(
            serverCertificate.ExportCertificatePem(),
            privateKey.ExportPkcs8PrivateKeyPem(),
            authority.ExportCertificatePem());

        Assert.Equal(serverCertificate.Thumbprint, credentials.Identity.Thumbprint);
        Assert.Single(credentials.TrustedAuthorities);
        Assert.Equal(authority.Thumbprint, credentials.TrustedAuthorities[0].Thumbprint);
    }

    /// <summary>Omitting the trusted authority PEM derives trust from the identity PEM's own issuance chain.</summary>
    [Fact]
    public void FromPemText_NoTrustedAuthorityPem_DerivesTrustedAuthoritiesFromCertificatePem()
    {
        (X509Certificate2 serverCertificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        X509Certificate2 authority = trustedAuthorities[0];
        using RSA privateKey = serverCertificate.GetRSAPrivateKey()!;
        string certificatePem = serverCertificate.ExportCertificatePem() + Environment.NewLine + authority.ExportCertificatePem();

        MsmtCredentials credentials = MsmtCredentials.FromPemText(certificatePem, privateKey.ExportPkcs8PrivateKeyPem());

        Assert.Equal(serverCertificate.Thumbprint, credentials.Identity.Thumbprint);
        Assert.Single(credentials.TrustedAuthorities);
        Assert.Equal(authority.Thumbprint, credentials.TrustedAuthorities[0].Thumbprint);
    }

    /// <summary>Loading from PEM streams reproduces the original identity certificate and trusted authority, leaving the streams open.</summary>
    [Fact]
    public void FromPemStreams_ValidStreams_LoadsIdentityAndDoesNotDisposeStreams()
    {
        (X509Certificate2 serverCertificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        X509Certificate2 authority = trustedAuthorities[0];
        using RSA privateKey = serverCertificate.GetRSAPrivateKey()!;

        using MemoryStream certificateStream = new(Encoding.UTF8.GetBytes(serverCertificate.ExportCertificatePem()));
        using MemoryStream privateKeyStream = new(Encoding.UTF8.GetBytes(privateKey.ExportPkcs8PrivateKeyPem()));
        using MemoryStream trustedAuthorityStream = new(Encoding.UTF8.GetBytes(authority.ExportCertificatePem()));

        MsmtCredentials credentials = MsmtCredentials.FromPemStreams(certificateStream, privateKeyStream, trustedAuthorityStream);

        Assert.Equal(serverCertificate.Thumbprint, credentials.Identity.Thumbprint);
        Assert.Single(credentials.TrustedAuthorities);
        Assert.Equal(authority.Thumbprint, credentials.TrustedAuthorities[0].Thumbprint);
        Assert.True(certificateStream.CanRead);
        Assert.True(privateKeyStream.CanRead);
        Assert.True(trustedAuthorityStream.CanRead);
    }

    /// <summary>Omitting the trusted authority stream derives trust from the identity stream's own issuance chain.</summary>
    [Fact]
    public void FromPemStreams_NoTrustedAuthorities_DerivesTrustedAuthoritiesFromCertificateStream()
    {
        (X509Certificate2 serverCertificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        X509Certificate2 authority = trustedAuthorities[0];
        using RSA privateKey = serverCertificate.GetRSAPrivateKey()!;
        string certificatePem = serverCertificate.ExportCertificatePem() + Environment.NewLine + authority.ExportCertificatePem();

        using MemoryStream certificateStream = new(Encoding.UTF8.GetBytes(certificatePem));
        using MemoryStream privateKeyStream = new(Encoding.UTF8.GetBytes(privateKey.ExportPkcs8PrivateKeyPem()));

        MsmtCredentials credentials = MsmtCredentials.FromPemStreams(certificateStream, privateKeyStream);

        Assert.Equal(serverCertificate.Thumbprint, credentials.Identity.Thumbprint);
        Assert.Single(credentials.TrustedAuthorities);
        Assert.Equal(authority.Thumbprint, credentials.TrustedAuthorities[0].Thumbprint);
    }

    /// <summary>Loading from PEM files reproduces the original identity certificate and trusted authority.</summary>
    [Fact]
    public void FromPemFiles_ValidFiles_LoadsIdentityAndTrustedAuthorities()
    {
        (X509Certificate2 serverCertificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        X509Certificate2 authority = trustedAuthorities[0];

        string directory = Directory.CreateTempSubdirectory("msmt-credentials-test").FullName;
        try
        {
            string certificatePath = Path.Combine(directory, "identity.pem");
            string privateKeyPath = Path.Combine(directory, "identity-key.pem");
            string trustedAuthorityPath = Path.Combine(directory, "ca.pem");

            File.WriteAllText(certificatePath, serverCertificate.ExportCertificatePem());
            using RSA privateKey = serverCertificate.GetRSAPrivateKey()!;
            File.WriteAllText(privateKeyPath, privateKey.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(trustedAuthorityPath, authority.ExportCertificatePem());

            MsmtCredentials credentials = MsmtCredentials.FromPemFiles(certificatePath, privateKeyPath, trustedAuthorityPath);

            Assert.Equal(serverCertificate.Thumbprint, credentials.Identity.Thumbprint);
            Assert.Single(credentials.TrustedAuthorities);
            Assert.Equal(authority.Thumbprint, credentials.TrustedAuthorities[0].Thumbprint);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Omitting the trusted authority path derives trust from the identity file's own issuance chain.</summary>
    [Fact]
    public void FromPemFiles_NoTrustedAuthorityPath_DerivesTrustedAuthoritiesFromIdentityFile()
    {
        (X509Certificate2 serverCertificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        X509Certificate2 authority = trustedAuthorities[0];

        string directory = Directory.CreateTempSubdirectory("msmt-credentials-test").FullName;
        try
        {
            string certificatePath = Path.Combine(directory, "identity.pem");
            string privateKeyPath = Path.Combine(directory, "identity-key.pem");

            File.WriteAllText(certificatePath, serverCertificate.ExportCertificatePem() + Environment.NewLine + authority.ExportCertificatePem());
            using RSA privateKey = serverCertificate.GetRSAPrivateKey()!;
            File.WriteAllText(privateKeyPath, privateKey.ExportPkcs8PrivateKeyPem());

            MsmtCredentials credentials = MsmtCredentials.FromPemFiles(certificatePath, privateKeyPath);

            Assert.Equal(serverCertificate.Thumbprint, credentials.Identity.Thumbprint);
            Assert.Single(credentials.TrustedAuthorities);
            Assert.Equal(authority.Thumbprint, credentials.TrustedAuthorities[0].Thumbprint);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Loading from the certificate store finds the identity certificate by subject name.</summary>
    [Fact]
    public void FromStore_MatchingSubjectName_LoadsIdentityAndTrustedAuthorities()
    {
        (X509Certificate2 serverCertificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        using X509Store store = new(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(serverCertificate);
        try
        {
            MsmtCredentials credentials = MsmtCredentials.FromStore("msmt-server", trustedAuthorities: trustedAuthorities);

            Assert.Equal(serverCertificate.Thumbprint, credentials.Identity.Thumbprint);
            Assert.Same(trustedAuthorities, credentials.TrustedAuthorities);
        }
        finally
        {
            store.Remove(serverCertificate);
        }
    }

    /// <summary>Omitting the trusted authorities derives trust from the identity certificate's own issuance chain.</summary>
    [Fact]
    public void FromStore_NoTrustedAuthorities_DerivesTrustedAuthoritiesFromIssuanceChain()
    {
        (X509Certificate2 serverCertificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();
        X509Certificate2 authority = trustedAuthorities[0];

        using X509Store identityStore = new(StoreName.My, StoreLocation.CurrentUser);
        identityStore.Open(OpenFlags.ReadWrite);
        identityStore.Add(serverCertificate);

        using X509Store authorityStore = new(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
        authorityStore.Open(OpenFlags.ReadWrite);
        authorityStore.Add(authority);
        try
        {
            MsmtCredentials credentials = MsmtCredentials.FromStore("msmt-server");

            Assert.Equal(serverCertificate.Thumbprint, credentials.Identity.Thumbprint);
            Assert.Contains(credentials.TrustedAuthorities, certificate => certificate.Thumbprint == authority.Thumbprint);
            Assert.DoesNotContain(credentials.TrustedAuthorities, certificate => certificate.Thumbprint == serverCertificate.Thumbprint);
        }
        finally
        {
            identityStore.Remove(serverCertificate);
            authorityStore.Remove(authority);
        }
    }

    /// <summary>Loading from the certificate store throws when no certificate matches the given subject name.</summary>
    [Fact]
    public void FromStore_NoMatch_Throws() =>
        Assert.Throws<InvalidOperationException>(() => MsmtCredentials.FromStore("msmt-nonexistent-certificate"));

    /// <summary>Loading from the certificate store throws when more than one certificate matches the given subject name.</summary>
    [Fact]
    public void FromStore_MultipleMatches_Throws()
    {
        (X509Certificate2 serverCertificate, _, _) = TestMsmtCertificates.Create();

        CertificateRequest duplicateRequest = new(serverCertificate.SubjectName, RSA.Create(2048), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        X509Certificate2 duplicate = duplicateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        using X509Store store = new(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        store.Add(serverCertificate);
        store.Add(duplicate);
        try
        {
            Assert.Throws<InvalidOperationException>(() => MsmtCredentials.FromStore("msmt-server"));
        }
        finally
        {
            store.Remove(serverCertificate);
            store.Remove(duplicate);
        }
    }
}
