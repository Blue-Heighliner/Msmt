namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// Bridges .NET <see cref="X509Certificate2"/>-based <see cref="MsmtCredentials"/> to the BouncyCastle
/// TLS engine's own certificate, key, and signature types, and implements the certificate trust and
/// signature algorithm selection shared by <see cref="MsmtTlsClient"/> and <see cref="MsmtTlsServer"/>.
/// </summary>
internal static class MsmtBcCryptography
{
    private static readonly short[] PreferredSignatureSchemes =
    [
        SignatureAlgorithm.rsa_pss_rsae_sha256,
        SignatureAlgorithm.rsa_pss_rsae_sha384,
        SignatureAlgorithm.rsa_pss_rsae_sha512,
    ];

    /// <summary>Gets the shared BouncyCastle TLS crypto provider used by every MSMT connection.</summary>
    public static TlsCrypto Crypto { get; } = new BcTlsCrypto();

    /// <summary>Gets the two TLS 1.3 cipher suites the MSMT ICD permits, in the required preference order.</summary>
    public static IReadOnlyList<int> CipherSuites { get; } = [CipherSuite.TLS_CHACHA20_POLY1305_SHA256, CipherSuite.TLS_AES_256_GCM_SHA384];

    /// <summary>
    /// Converts a node's own identity certificate into the certificate chain and private key types the
    /// BouncyCastle TLS engine signs and presents with.
    /// </summary>
    /// <param name="identity">The identity certificate, including its RSA private key.</param>
    /// <returns>The equivalent BouncyCastle certificate chain and private key.</returns>
    /// <exception cref="NotSupportedException"><paramref name="identity"/> does not carry an RSA private key.</exception>
    public static (Certificate Chain, AsymmetricKeyParameter PrivateKey) ToBcIdentity(X509Certificate2 identity)
    {
        TlsCertificate certificate = Crypto.CreateCertificate(identity.RawData);
        Certificate chain = new(TlsUtilities.EmptyBytes, [new CertificateEntry(certificate, null)]);

        using System.Security.Cryptography.RSA privateKey = identity.GetRSAPrivateKey()
            ?? throw new NotSupportedException("Only RSA MSMT identity certificates are supported.");

        return (chain, PrivateKeyFactory.CreateKey(privateKey.ExportPkcs8PrivateKey()));
    }

    /// <summary>Gets the minimum RSA modulus size, in bits, this implementation accepts on a peer's certificate, per NIST SP 800-131A Revision 2.</summary>
    public static int MinimumRsaKeySizeBits { get; } = 2048;

    /// <summary>
    /// Determines whether a peer's certificate chain is currently valid, chains to one of a set of
    /// trusted certificate authorities (ignoring any intermediates the peer itself did not present, per
    /// <paramref name="peerChain"/>'s own certificates), carries an RSA key of at least <see
    /// cref="MinimumRsaKeySizeBits"/>, and - against any already-cached CRL - has not been revoked.
    /// Revocation checking is offline only (no live CRL/OCSP network fetch), since MSMT deployments
    /// commonly operate on networks without connectivity to a CRL or OCSP responder and Message Mode's
    /// per-message handshake would otherwise pay that network round trip's latency on every single send.
    /// </summary>
    /// <param name="peerChain">The peer's certificate chain, as received over TLS.</param>
    /// <param name="trustedAuthorities">The trusted certificate authorities.</param>
    /// <returns><see langword="true"/> if the chain's leaf certificate is valid and trusted.</returns>
    public static bool IsTrusted(Certificate peerChain, X509Certificate2Collection trustedAuthorities)
    {
        if (peerChain.IsEmpty)
        {
            return false;
        }

        TlsCertificate[] certificates = peerChain.GetCertificateList();
        using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(certificates[0].GetEncoded());

        using System.Security.Cryptography.RSA? publicKey = leaf.GetRSAPublicKey();
        if (publicKey is null || publicKey.KeySize < MinimumRsaKeySizeBits)
        {
            return false;
        }

        using X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(trustedAuthorities);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Offline;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
        chain.ChainPolicy.VerificationFlags =
            X509VerificationFlags.IgnoreEndRevocationUnknown
            | X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown
            | X509VerificationFlags.IgnoreRootRevocationUnknown;

        List<X509Certificate2> intermediates = [];
        try
        {
            for (int index = 1; index < certificates.Length; index++)
            {
                X509Certificate2 intermediate = X509CertificateLoader.LoadCertificate(certificates[index].GetEncoded());
                intermediates.Add(intermediate);
                chain.ChainPolicy.ExtraStore.Add(intermediate);
            }

            return chain.Build(leaf);
        }
        finally
        {
            foreach (X509Certificate2 intermediate in intermediates)
            {
                intermediate.Dispose();
            }
        }
    }

    /// <summary>
    /// Converts a peer's leaf certificate, as received over TLS, into a .NET <see cref="X509Certificate2"/>
    /// for applications to inspect.
    /// </summary>
    /// <param name="peerChain">The peer's certificate chain, as received over TLS.</param>
    /// <returns>The equivalent leaf certificate.</returns>
    public static X509Certificate2 ToNetCertificate(Certificate peerChain) =>
        X509CertificateLoader.LoadCertificate(peerChain.GetCertificateList()[0].GetEncoded());

    /// <summary>
    /// Determines whether a server name matches a certificate's identity, per RFC 6125: matched against
    /// the certificate's Subject Alternative Name IP address entries (exact match) or DNS name entries
    /// (case-insensitive, permitting a single leftmost wildcard label), or, when the certificate carries no
    /// Subject Alternative Name extension at all, against its Common Name.
    /// </summary>
    /// <param name="leaf">The peer's leaf certificate.</param>
    /// <param name="serverName">The expected server name, as an IP address or DNS hostname.</param>
    /// <returns><see langword="true"/> if <paramref name="serverName"/> matches the certificate's identity.</returns>
    public static bool MatchesServerName(X509Certificate2 leaf, string serverName)
    {
        X509SubjectAlternativeNameExtension? subjectAlternativeName =
            leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();

        if (subjectAlternativeName is null)
        {
            return string.Equals(leaf.GetNameInfo(X509NameType.SimpleName, false), serverName, StringComparison.OrdinalIgnoreCase);
        }

        if (IPAddress.TryParse(serverName, out IPAddress? serverAddress))
        {
            return subjectAlternativeName.EnumerateIPAddresses().Any(address => address.Equals(serverAddress));
        }

        return subjectAlternativeName.EnumerateDnsNames().Any(dnsName => MatchesDnsName(dnsName, serverName));
    }

    /// <summary>
    /// Selects an RSA-PSS signature algorithm from a peer-supplied candidate list, matching MSMT's
    /// RSA-only certificate profile under TLS 1.3.
    /// </summary>
    /// <param name="candidates">The peer's supported signature algorithms, or <see langword="null"/> if none were advertised.</param>
    /// <returns>The selected algorithm, or <see langword="null"/> if no RSA-PSS scheme was offered.</returns>
    public static SignatureAndHashAlgorithm? SelectSignatureAlgorithm(IList<SignatureAndHashAlgorithm>? candidates)
    {
        if (candidates is null)
        {
            return null;
        }

        foreach (short preferred in PreferredSignatureSchemes)
        {
            foreach (SignatureAndHashAlgorithm candidate in candidates)
            {
                if (candidate.Signature == preferred)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool MatchesDnsName(string pattern, string serverName)
    {
        if (!pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            return string.Equals(pattern, serverName, StringComparison.OrdinalIgnoreCase);
        }

        int firstLabelEnd = serverName.IndexOf('.');
        return firstLabelEnd >= 0 && string.Equals(pattern[2..], serverName[(firstLabelEnd + 1)..], StringComparison.OrdinalIgnoreCase);
    }
}
