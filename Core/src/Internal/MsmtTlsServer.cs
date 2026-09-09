namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// The BouncyCastle <see cref="TlsServer"/> implementation backing <see cref="MsmtServer"/>, pinning
/// the connection to the MSMT ICD's TLS configuration and enforcing mutual certificate authentication.
/// </summary>
internal sealed class MsmtTlsServer(MsmtHostOptions options) : DefaultTlsServer(MsmtBcCryptography.Crypto)
{
    /// <summary>Gets the client's identity, once <see cref="NotifyClientCertificate"/> has verified its certificate; <see langword="null"/> beforehand.</summary>
    public MsmtIdentity? ClientIdentity { get; private set; }

    /// <inheritdoc />
    protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.TLSv13.Only();

    /// <inheritdoc />
    protected override int[] GetSupportedCipherSuites() =>
        TlsUtilities.GetSupportedCipherSuites(Crypto, [.. MsmtBcCryptography.CipherSuites]);

    /// <inheritdoc />
    public override void ProcessClientExtensions(IDictionary<int, byte[]> clientExtensions)
    {
        base.ProcessClientExtensions(clientExtensions);

        IList<ServerName>? serverNames = clientExtensions is null ? null : TlsExtensionsUtilities.GetServerNameExtensionClient(clientExtensions);
        ServerName? hostName = serverNames?.FirstOrDefault(name => name.NameType == NameType.host_name);

        if (hostName is null || !IsValidHostname(hostName.NameData))
        {
            throw new TlsFatalAlert(AlertDescription.unrecognized_name);
        }
    }

    /// <summary>
    /// Determines whether <paramref name="nameData"/> is an acceptable "server_name" value. When <see
    /// cref="MsmtHostOptions.RequireFullyQualifiedHostname"/> is enabled, only a fully qualified DNS
    /// hostname is accepted, per the ICD; otherwise, any syntactically plausible value is accepted,
    /// including an IP address literal, per RFC 952/1123 hostname syntax.
    /// </summary>
    /// <param name="nameData">The raw "server_name" bytes presented in the ClientHello.</param>
    /// <returns><see langword="true"/> if <paramref name="nameData"/> decodes to an acceptable hostname.</returns>
    private bool IsValidHostname(byte[] nameData)
    {
        if (nameData.Length is 0 or > 255)
        {
            return false;
        }

        UriHostNameType type = Uri.CheckHostName(Encoding.ASCII.GetString(nameData));
        return options.RequireFullyQualifiedHostname ? type == UriHostNameType.Dns : type != UriHostNameType.Unknown;
    }

    /// <inheritdoc />
    public override CertificateRequest GetCertificateRequest() =>
        new(TlsUtilities.EmptyBytes, TlsUtilities.GetDefaultSupportedSignatureAlgorithms(m_context), null, null);

    /// <inheritdoc />
    public override TlsCredentials GetCredentials()
    {
        SignatureAndHashAlgorithm? signatureAndHashAlgorithm =
            MsmtBcCryptography.SelectSignatureAlgorithm(m_context.SecurityParameters.ClientSigAlgs);
        if (signatureAndHashAlgorithm is null)
        {
            throw new TlsFatalAlert(AlertDescription.handshake_failure);
        }

        (Certificate chain, AsymmetricKeyParameter privateKey) = MsmtBcCryptography.ToBcIdentity(options.Credentials.Identity);
        return new BcDefaultTlsCredentialedSigner(new TlsCryptoParameters(m_context), (BcTlsCrypto)m_context.Crypto, privateKey, chain, signatureAndHashAlgorithm);
    }

    /// <inheritdoc />
    public override void NotifyClientCertificate(Certificate clientCertificate)
    {
        if (!MsmtBcCryptography.IsTrusted(clientCertificate, options.Credentials.TrustedAuthorities))
        {
            throw new TlsFatalAlert(AlertDescription.bad_certificate);
        }

        using X509Certificate2 certificate = MsmtBcCryptography.ToNetCertificate(clientCertificate);
        ClientIdentity = MsmtIdentity.FromCertificate(certificate);
    }
}
