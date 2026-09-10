namespace BlueHeighliner.Msmt.Tests.Unit;

/// <summary>Unit tests for <see cref="MsmtBcCryptography"/>.</summary>
public sealed class MsmtBcCryptographyTests
{
    /// <summary>An empty certificate chain is never trusted.</summary>
    [Fact]
    public void IsTrusted_EmptyChain_ReturnsFalse()
    {
        Certificate emptyChain = new([]);
        (_, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        Assert.False(MsmtBcCryptography.IsTrusted(emptyChain, trustedAuthorities));
    }

    /// <summary>A certificate signed by a certificate authority outside the trusted set is not trusted.</summary>
    [Fact]
    public void IsTrusted_SignedByUntrustedAuthority_ReturnsFalse()
    {
        (X509Certificate2 serverCertificate, _, _) = TestMsmtCertificates.Create();
        (_, _, X509Certificate2Collection otherTrustedAuthorities) = TestMsmtCertificates.Create();

        Assert.False(MsmtBcCryptography.IsTrusted(ToChain(serverCertificate), otherTrustedAuthorities));
    }

    /// <summary>An expired certificate is not trusted, even if signed by a trusted authority.</summary>
    [Fact]
    public void IsTrusted_ExpiredCertificate_ReturnsFalse()
    {
        (X509Certificate2 expiredServer, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.CreateExpiredServer();

        Assert.False(MsmtBcCryptography.IsTrusted(ToChain(expiredServer), trustedAuthorities));
    }

    /// <summary>A currently-valid certificate signed by a trusted authority is trusted.</summary>
    [Fact]
    public void IsTrusted_ValidAndSignedByTrustedAuthority_ReturnsTrue()
    {
        (X509Certificate2 serverCertificate, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        Assert.True(MsmtBcCryptography.IsTrusted(ToChain(serverCertificate), trustedAuthorities));
    }

    /// <summary>A leaf certificate is trusted when the peer presents its signing intermediate authority alongside it, even though only the root authority above that intermediate is in the trusted set.</summary>
    [Fact]
    public void IsTrusted_ValidChainThroughPresentedIntermediateAuthority_ReturnsTrue()
    {
        (X509Certificate2 leaf, X509Certificate2 intermediate, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.CreateWithIntermediateAuthority();

        Certificate chain = new(
            TlsUtilities.EmptyBytes,
            [
                new CertificateEntry(MsmtBcCryptography.Crypto.CreateCertificate(leaf.RawData), null),
                new CertificateEntry(MsmtBcCryptography.Crypto.CreateCertificate(intermediate.RawData), null),
            ]);

        Assert.True(MsmtBcCryptography.IsTrusted(chain, trustedAuthorities));
    }

    /// <summary>A leaf certificate signed by an intermediate authority is not trusted if the peer presents only the leaf, omitting the intermediate needed to chain up to the trusted root.</summary>
    [Fact]
    public void IsTrusted_ValidChainWithoutPresentingIntermediateAuthority_ReturnsFalse()
    {
        (X509Certificate2 leaf, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.CreateWithIntermediateAuthority();

        Assert.False(MsmtBcCryptography.IsTrusted(ToChain(leaf), trustedAuthorities));
    }

    /// <summary>A certificate carrying a non-RSA (e.g. ECDSA) public key is not trusted, matching MSMT's RSA-only certificate profile.</summary>
    [Fact]
    public void IsTrusted_NonRsaCertificate_ReturnsFalse()
    {
        X509Certificate2 ecdsaCertificate = TestMsmtCertificates.CreateEcdsaOnly();
        (_, _, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.Create();

        Assert.False(MsmtBcCryptography.IsTrusted(ToChain(ecdsaCertificate), trustedAuthorities));
    }

    /// <summary>A certificate with an RSA key below <see cref="MsmtBcCryptography.MinimumRsaKeySizeBits"/> is not trusted, even if otherwise valid and signed by a trusted authority.</summary>
    [Fact]
    public void IsTrusted_RsaKeyBelowMinimumSize_ReturnsFalse()
    {
        (X509Certificate2 weakKeyServer, X509Certificate2Collection trustedAuthorities) = TestMsmtCertificates.CreateWeakKeyServer();

        Assert.False(MsmtBcCryptography.IsTrusted(ToChain(weakKeyServer), trustedAuthorities));
    }

    /// <summary>With no advertised signature algorithms, no algorithm can be selected.</summary>
    [Fact]
    public void SelectSignatureAlgorithm_NullCandidates_ReturnsNull()
    {
        Assert.Null(MsmtBcCryptography.SelectSignatureAlgorithm(null));
    }

    /// <summary>An advertised list containing no RSA-PSS scheme yields no selection.</summary>
    [Fact]
    public void SelectSignatureAlgorithm_NoRsaPssCandidate_ReturnsNull()
    {
        List<SignatureAndHashAlgorithm> candidates = [SignatureAndHashAlgorithm.GetInstance(Org.BouncyCastle.Tls.HashAlgorithm.sha256, SignatureAlgorithm.ecdsa)];

        Assert.Null(MsmtBcCryptography.SelectSignatureAlgorithm(candidates));
    }

    /// <summary>An advertised RSA-PSS scheme is selected.</summary>
    [Fact]
    public void SelectSignatureAlgorithm_RsaPssCandidatePresent_ReturnsIt()
    {
        SignatureAndHashAlgorithm expected = SignatureAndHashAlgorithm.GetInstance(Org.BouncyCastle.Tls.HashAlgorithm.sha384, SignatureAlgorithm.rsa_pss_rsae_sha384);
        List<SignatureAndHashAlgorithm> candidates = [expected];

        Assert.Same(expected, MsmtBcCryptography.SelectSignatureAlgorithm(candidates));
    }

    /// <summary>An RSA identity certificate converts to a non-empty BouncyCastle chain and key.</summary>
    [Fact]
    public void ToBcIdentity_RsaCertificate_ReturnsChainAndKey()
    {
        (X509Certificate2 serverCertificate, _, _) = TestMsmtCertificates.Create();

        (Certificate chain, AsymmetricKeyParameter privateKey) = MsmtBcCryptography.ToBcIdentity(serverCertificate);

        Assert.False(chain.IsEmpty);
        Assert.NotNull(privateKey);
    }

    /// <summary>A non-RSA identity certificate is rejected.</summary>
    [Fact]
    public void ToBcIdentity_NonRsaCertificate_Throws()
    {
        X509Certificate2 ecdsaCertificate = TestMsmtCertificates.CreateEcdsaOnly();

        Assert.Throws<NotSupportedException>(() => MsmtBcCryptography.ToBcIdentity(ecdsaCertificate));
    }

    /// <summary>A server name matching a certificate's Subject Alternative Name IP address entry matches.</summary>
    [Fact]
    public void MatchesServerName_MatchingSubjectAlternativeNameIpAddress_ReturnsTrue()
    {
        (X509Certificate2 serverCertificate, _, _) = TestMsmtCertificates.Create();

        Assert.True(MsmtBcCryptography.MatchesServerName(serverCertificate, "127.0.0.1"));
    }

    /// <summary>A server name not present among a certificate's Subject Alternative Name entries does not match.</summary>
    [Fact]
    public void MatchesServerName_NonMatchingSubjectAlternativeName_ReturnsFalse()
    {
        (X509Certificate2 serverCertificate, _, _) = TestMsmtCertificates.Create();

        Assert.False(MsmtBcCryptography.MatchesServerName(serverCertificate, "10.0.0.1"));
    }

    /// <summary>A server name matching a certificate's Subject Alternative Name wildcard DNS entry matches.</summary>
    [Fact]
    public void MatchesServerName_MatchingWildcardDnsName_ReturnsTrue()
    {
        X509Certificate2 certificate = CreateWithDnsSubjectAlternativeName("*.example.com");

        Assert.True(MsmtBcCryptography.MatchesServerName(certificate, "server.example.com"));
    }

    /// <summary>A wildcard Subject Alternative Name entry does not match beyond its single leftmost label.</summary>
    [Fact]
    public void MatchesServerName_WildcardDnsNameWithExtraLabel_ReturnsFalse()
    {
        X509Certificate2 certificate = CreateWithDnsSubjectAlternativeName("*.example.com");

        Assert.False(MsmtBcCryptography.MatchesServerName(certificate, "sub.server.example.com"));
    }

    /// <summary>With no Subject Alternative Name extension at all, a server name matching the certificate's Common Name falls back to matching.</summary>
    [Fact]
    public void MatchesServerName_NoSubjectAlternativeNameMatchingCommonName_ReturnsTrue()
    {
        (X509Certificate2 expiredServer, _) = TestMsmtCertificates.CreateExpiredServer();

        Assert.True(MsmtBcCryptography.MatchesServerName(expiredServer, "msmt-expired-server"));
    }

    /// <summary>A server name matching a certificate's literal (non-wildcard) Subject Alternative Name DNS entry matches, case-insensitively.</summary>
    [Fact]
    public void MatchesServerName_MatchingLiteralDnsName_ReturnsTrue()
    {
        X509Certificate2 certificate = CreateWithDnsSubjectAlternativeName("msmt-literal.example.com");

        Assert.True(MsmtBcCryptography.MatchesServerName(certificate, "MSMT-LITERAL.example.com"));
    }

    /// <summary>A server name not matching a certificate's literal (non-wildcard) Subject Alternative Name DNS entry does not match.</summary>
    [Fact]
    public void MatchesServerName_NonMatchingLiteralDnsName_ReturnsFalse()
    {
        X509Certificate2 certificate = CreateWithDnsSubjectAlternativeName("msmt-literal.example.com");

        Assert.False(MsmtBcCryptography.MatchesServerName(certificate, "other.example.com"));
    }

    private static X509Certificate2 CreateWithDnsSubjectAlternativeName(string dnsName)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=msmt-wildcard", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        SubjectAlternativeNameBuilder subjectAlternativeName = new();
        subjectAlternativeName.AddDnsName(dnsName);
        request.CertificateExtensions.Add(subjectAlternativeName.Build());

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static Certificate ToChain(X509Certificate2 certificate) =>
        new(TlsUtilities.EmptyBytes, [new CertificateEntry(MsmtBcCryptography.Crypto.CreateCertificate(certificate.RawData), null)]);
}
