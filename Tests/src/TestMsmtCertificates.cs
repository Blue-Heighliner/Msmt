namespace BlueHeighliner.Msmt.Tests;

/// <summary>Generates an ephemeral, in-memory certificate authority and leaf certificates for MSMT TLS tests.</summary>
internal static class TestMsmtCertificates
{
    /// <summary>Creates a server certificate, a client certificate, and the CA collection that trusts both.</summary>
    /// <returns>The generated server certificate, client certificate, and trusted authority collection.</returns>
    public static (X509Certificate2 Server, X509Certificate2 Client, X509Certificate2Collection TrustedAuthorities) Create()
    {
        (X509Certificate2 authority, RSA authorityKey) = CreateAuthority();
        using (authorityKey)
        {
            // Both certificates carry the loopback SAN: MSMT is peer-to-peer, so either identity may act as
            // the TLS server (whose hostname is validated) depending on which side receives the connection.
            X509Certificate2 server = CreateLeaf(authority, authorityKey, "CN=msmt-server", DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1), serverName: "127.0.0.1");
            X509Certificate2 client = CreateLeaf(authority, authorityKey, "CN=msmt-client", DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1), serverName: "127.0.0.1");

            return (server, client, [authority]);
        }
    }

    /// <summary>Creates a server certificate that has already expired, plus the CA collection that trusts it.</summary>
    /// <returns>The expired server certificate and its trusted authority collection.</returns>
    public static (X509Certificate2 Server, X509Certificate2Collection TrustedAuthorities) CreateExpiredServer()
    {
        (X509Certificate2 authority, RSA authorityKey) = CreateAuthority();
        using (authorityKey)
        {
            X509Certificate2 server = CreateLeaf(authority, authorityKey, "CN=msmt-expired-server", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));

            return (server, [authority]);
        }
    }

    /// <summary>Creates a server certificate with an RSA key below MSMT's minimum accepted key size, plus the CA collection that trusts it.</summary>
    /// <returns>The weak-key server certificate and its trusted authority collection.</returns>
    public static (X509Certificate2 Server, X509Certificate2Collection TrustedAuthorities) CreateWeakKeyServer()
    {
        (X509Certificate2 authority, RSA authorityKey) = CreateAuthority();
        using (authorityKey)
        {
            X509Certificate2 server = CreateLeaf(authority, authorityKey, "CN=msmt-weak-key-server", DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1), keySizeBits: 1024);

            return (server, [authority]);
        }
    }

    /// <summary>Creates a leaf certificate signed by an intermediate authority, itself signed by a root authority, plus the collection trusting only the root.</summary>
    /// <returns>The leaf and intermediate certificates, and the collection trusting only the root authority.</returns>
    public static (X509Certificate2 Leaf, X509Certificate2 Intermediate, X509Certificate2Collection TrustedAuthorities) CreateWithIntermediateAuthority()
    {
        (X509Certificate2 root, RSA rootKey) = CreateAuthority();
        using (rootKey)
        {
            RSA intermediateKey = RSA.Create(2048);
            CertificateRequest intermediateRequest = new("CN=Test MSMT Intermediate CA", intermediateKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));

            byte[] intermediateSerial = RandomNumberGenerator.GetBytes(16);
            using X509Certificate2 intermediateWithoutKey = intermediateRequest.Create(
                root.SubjectName,
                X509SignatureGenerator.CreateForRSA(rootKey, RSASignaturePadding.Pkcs1),
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddDays(1),
                intermediateSerial);
            X509Certificate2 intermediate = intermediateWithoutKey.CopyWithPrivateKey(intermediateKey);

            X509Certificate2 leaf = CreateLeaf(intermediate, intermediateKey, "CN=msmt-leaf-via-intermediate", DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

            return (leaf, intermediate, [root]);
        }
    }

    /// <summary>Creates a self-signed certificate using an ECDSA key, which MSMT's RSA-only identity conversion does not support.</summary>
    /// <returns>The generated ECDSA certificate.</returns>
    public static X509Certificate2 CreateEcdsaOnly()
    {
        using ECDsa key = ECDsa.Create();
        CertificateRequest request = new("CN=msmt-ecdsa", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static (X509Certificate2 Authority, RSA AuthorityKey) CreateAuthority()
    {
        RSA authorityKey = RSA.Create(2048);
        CertificateRequest authorityRequest = new("CN=Test MSMT CA", authorityKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        X509Certificate2 authority = authorityRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        return (authority, authorityKey);
    }

    private static X509Certificate2 CreateLeaf(X509Certificate2 authority, RSA authorityKey, string subject, DateTimeOffset notBefore, DateTimeOffset notAfter, int keySizeBits = 2048, string? serverName = null)
    {
        RSA leafKey = RSA.Create(keySizeBits);
        CertificateRequest request = new(subject, leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));

        if (serverName is not null)
        {
            SubjectAlternativeNameBuilder subjectAlternativeName = new();
            if (IPAddress.TryParse(serverName, out IPAddress? address))
            {
                subjectAlternativeName.AddIpAddress(address);
            }
            else
            {
                subjectAlternativeName.AddDnsName(serverName);
            }

            request.CertificateExtensions.Add(subjectAlternativeName.Build());
        }

        byte[] serialNumber = RandomNumberGenerator.GetBytes(16);
        using X509Certificate2 leaf = request.Create(
            authority.SubjectName,
            X509SignatureGenerator.CreateForRSA(authorityKey, RSASignaturePadding.Pkcs1),
            notBefore,
            notAfter,
            serialNumber);

        return leaf.CopyWithPrivateKey(leafKey);
    }
}
