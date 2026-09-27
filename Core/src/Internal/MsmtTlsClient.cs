namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// The BouncyCastle <see cref="TlsClient"/> implementation used by every connection this library
/// initiates, pinning it to the MSMT ICD's TLS configuration and performing mutual certificate
/// authentication.
/// </summary>
/// <param name="credentials">This side's identity and the certificate authorities it trusts.</param>
/// <param name="target">The remote peer, whose server name is presented via SNI and validated against its certificate.</param>
internal sealed class MsmtTlsClient(MsmtCredentials credentials, MsmtNameTarget target) : DefaultTlsClient(MsmtBcCryptography.Crypto)
{
    /// <summary>Gets the server's identity, once <see cref="Authentication.NotifyServerCertificate"/> has verified its certificate; <see langword="null"/> beforehand.</summary>
    public MsmtIdentity? ServerIdentity { get; private set; }

    /// <inheritdoc />
    protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.TLSv13.Only();

    /// <inheritdoc />
    protected override int[] GetSupportedCipherSuites() =>
        TlsUtilities.GetSupportedCipherSuites(Crypto, [.. MsmtBcCryptography.CipherSuites]);

    /// <inheritdoc />
    protected override IList<ServerName> GetSniServerNames() =>
        [new ServerName(NameType.host_name, Encoding.ASCII.GetBytes(target.ServerName))];

    /// <inheritdoc />
    public override TlsAuthentication GetAuthentication() => new Authentication(m_context, credentials, target, this);

    /// <summary>
    /// Verifies the server's certificate against the trusted certificate authorities and supplies this
    /// client's own certificate and key when the server requests mutual authentication.
    /// </summary>
    private sealed class Authentication(TlsContext context, MsmtCredentials credentials, MsmtNameTarget target, MsmtTlsClient client) : TlsAuthentication
    {
        /// <inheritdoc />
        public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
            if (!MsmtBcCryptography.IsTrusted(serverCertificate.Certificate, credentials.TrustedAuthorities))
            {
                throw new TlsFatalAlert(AlertDescription.bad_certificate);
            }

            using X509Certificate2 leaf = MsmtBcCryptography.ToNetCertificate(serverCertificate.Certificate);
            if (!MsmtBcCryptography.MatchesServerName(leaf, target.ServerName))
            {
                throw new TlsFatalAlert(AlertDescription.bad_certificate);
            }

            client.ServerIdentity = MsmtIdentity.FromCertificate(leaf);
        }

        /// <inheritdoc />
        public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest)
        {
            SignatureAndHashAlgorithm? signatureAndHashAlgorithm =
                MsmtBcCryptography.SelectSignatureAlgorithm(certificateRequest.SupportedSignatureAlgorithms);
            if (signatureAndHashAlgorithm is null)
            {
                throw new TlsFatalAlert(AlertDescription.handshake_failure);
            }

            (Certificate chain, AsymmetricKeyParameter privateKey) = MsmtBcCryptography.ToBcIdentity(credentials.Identity);
            return new BcDefaultTlsCredentialedSigner(new TlsCryptoParameters(context), (BcTlsCrypto)context.Crypto, privateKey, chain, signatureAndHashAlgorithm);
        }
    }
}
