namespace BlueHeighliner.Msmt;

/// <summary>
/// The certificate-based identity and trust material an MSMT client or server authenticates with.
/// </summary>
/// <remarks>
/// MSMT's pre-shared-key authentication mode is not implemented by this client/server; only
/// certificate-based mutual authentication is offered. <see cref="Identity"/> must carry an RSA key,
/// the only key type this implementation converts into the BouncyCastle TLS engine's own credential
/// types.
/// </remarks>
public sealed record MsmtCredentials
{
    /// <summary>
    /// Loads credentials from PEM-encoded certificate, private key, and trusted authority text.
    /// </summary>
    /// <param name="certificatePem">This node's PEM-encoded identity certificate.</param>
    /// <param name="privateKeyPem">This node's PEM-encoded private key.</param>
    /// <param name="trustedAuthorityPem">
    /// PEM text containing one or more trusted CA certificates. When <see langword="null"/>, the
    /// trusted authorities are instead derived from <paramref name="certificatePem"/> itself, using
    /// every certificate in that text other than the identity certificate's own leaf entry.
    /// </param>
    /// <returns>The loaded credentials.</returns>
    public static MsmtCredentials FromPemText(string certificatePem, string privateKeyPem, string? trustedAuthorityPem = null)
    {
        X509Certificate2 identity = X509Certificate2.CreateFromPem(certificatePem, privateKeyPem);

        X509Certificate2Collection loaded = [];
        loaded.ImportFromPem(trustedAuthorityPem ?? certificatePem);

        X509Certificate2Collection trustedAuthorities = trustedAuthorityPem is not null
            ? loaded
            : [.. loaded.Where(certificate => !certificate.RawData.AsSpan().SequenceEqual(identity.RawData))];

        return new MsmtCredentials
        {
            Identity = identity,
            TrustedAuthorities = trustedAuthorities,
        };
    }

    /// <summary>
    /// Loads credentials by reading PEM-encoded certificate, private key, and trusted authority text
    /// from streams. Each stream is read to its end but not disposed; the caller retains ownership.
    /// </summary>
    /// <param name="certificate">A stream containing this node's PEM-encoded identity certificate.</param>
    /// <param name="privateKey">A stream containing this node's PEM-encoded private key.</param>
    /// <param name="trustedAuthorities">
    /// A stream containing one or more PEM-encoded trusted CA certificates. When <see langword="null"/>,
    /// the trusted authorities are instead derived from <paramref name="certificate"/> itself, using
    /// every certificate in that stream other than the identity certificate's own leaf entry.
    /// </param>
    /// <returns>The loaded credentials.</returns>
    public static MsmtCredentials FromPemStreams(Stream certificate, Stream privateKey, Stream? trustedAuthorities = null)
    {
        string certificatePem = ReadToEnd(certificate);
        string privateKeyPem = ReadToEnd(privateKey);
        string? trustedAuthorityPem = trustedAuthorities is null ? null : ReadToEnd(trustedAuthorities);

        return FromPemText(certificatePem, privateKeyPem, trustedAuthorityPem);
    }

    /// <summary>
    /// Loads credentials from PEM-encoded files on disk, mirroring the certificate/key/CA file layout
    /// used by the reference MSMT implementation.
    /// </summary>
    /// <param name="certificatePath">Path to this node's PEM-encoded identity certificate.</param>
    /// <param name="privateKeyPath">Path to this node's PEM-encoded private key.</param>
    /// <param name="trustedAuthorityPath">
    /// Path to a PEM file containing one or more trusted CA certificates. When <see langword="null"/>,
    /// the trusted authorities are instead derived from <paramref name="certificatePath"/> itself, using
    /// every certificate in that file other than the identity certificate's own leaf entry.
    /// </param>
    /// <returns>The loaded credentials.</returns>
    public static MsmtCredentials FromPemFiles(string certificatePath, string privateKeyPath, string? trustedAuthorityPath = null)
    {
        using FileStream certificateStream = File.OpenRead(certificatePath);
        using FileStream privateKeyStream = File.OpenRead(privateKeyPath);
        using FileStream? trustedAuthorityStream = trustedAuthorityPath is null ? null : File.OpenRead(trustedAuthorityPath);

        return FromPemStreams(certificateStream, privateKeyStream, trustedAuthorityStream);
    }

    /// <summary>
    /// Loads this node's identity certificate, including its private key, from a named certificate in
    /// the system's X.509 certificate store.
    /// </summary>
    /// <param name="subjectName">
    /// The subject name of the identity certificate to find in the store, e.g. <c>"msmt-server"</c> for
    /// a certificate whose subject is <c>CN=msmt-server</c>. Matched as a partial, case-insensitive
    /// match against the certificate's simple subject name, per <see cref="X509FindType.FindBySubjectName"/>.
    /// </param>
    /// <param name="storeName">The store to search. Defaults to the personal (<c>My</c>) store.</param>
    /// <param name="storeLocation">The store location to search. Defaults to <see cref="StoreLocation.CurrentUser"/>.</param>
    /// <param name="trustedAuthorities">
    /// The certificate authorities to trust for the remote peer's identity certificate. When
    /// <see langword="null"/>, the trusted authorities are instead derived from the identity
    /// certificate's own issuance chain, built against the system's certificate stores.
    /// </param>
    /// <returns>The loaded credentials.</returns>
    /// <exception cref="InvalidOperationException">
    /// No certificate with a matching subject name, or more than one, was found in the store.
    /// </exception>
    public static MsmtCredentials FromStore(string subjectName, StoreName storeName = StoreName.My, StoreLocation storeLocation = StoreLocation.CurrentUser, X509Certificate2Collection? trustedAuthorities = null)
    {
        using X509Store store = new(storeName, storeLocation);
        store.Open(OpenFlags.ReadOnly);

        X509Certificate2Collection matches = store.Certificates.Find(X509FindType.FindBySubjectName, subjectName, validOnly: false);

        if (matches.Count == 0)
        {
            throw new InvalidOperationException($"No certificate named '{subjectName}' was found in the {storeLocation}/{storeName} certificate store.");
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException($"More than one certificate named '{subjectName}' was found in the {storeLocation}/{storeName} certificate store.");
        }

        X509Certificate2 identity = matches[0];

        return new MsmtCredentials
        {
            Identity = identity,
            TrustedAuthorities = trustedAuthorities ?? DeriveTrustedAuthoritiesFromIssuanceChain(identity),
        };
    }

    /// <summary>Reads a stream to its end as text, without disposing the stream.</summary>
    /// <param name="stream">The stream to read.</param>
    /// <returns>The text read from the stream.</returns>
    private static string ReadToEnd(Stream stream)
    {
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Builds the certificate's issuance chain against the system's certificate stores and returns every
    /// certificate in it other than the leaf itself.
    /// </summary>
    /// <param name="identity">The certificate to build the issuance chain for.</param>
    /// <returns>The certificate authorities found in the chain, excluding <paramref name="identity"/>.</returns>
    private static X509Certificate2Collection DeriveTrustedAuthoritiesFromIssuanceChain(X509Certificate2 identity)
    {
        using X509Chain chain = new();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Offline;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.Build(identity);

        return [.. chain.ChainElements
            .Select(element => element.Certificate)
            .Where(certificate => !certificate.RawData.AsSpan().SequenceEqual(identity.RawData))];
    }

    /// <summary>Gets this node's own identity certificate, including its private key.</summary>
    public required X509Certificate2 Identity { get; init; }

    /// <summary>Gets the certificate authorities trusted to sign the remote peer's identity certificate.</summary>
    public required X509Certificate2Collection TrustedAuthorities { get; init; }
}
