namespace BlueHeighliner.Msmt;

/// <summary>
/// A snapshot of the commonly used identifying strings from an X.509 certificate, extracted once during
/// the TLS handshake so the certificate itself doesn't need to be kept alive - and can be disposed - for
/// as long as the identity is needed.
/// </summary>
public sealed record MsmtIdentity
{
    /// <summary>Extracts an identity snapshot from a certificate.</summary>
    /// <param name="certificate">The certificate to extract the identity from.</param>
    /// <returns>The extracted identity.</returns>
    public static MsmtIdentity FromCertificate(X509Certificate2 certificate) =>
        new()
        {
            Subject = certificate.Subject,
            Issuer = certificate.Issuer,
            SerialNumber = certificate.SerialNumber,
            Thumbprint = certificate.Thumbprint,
        };

    /// <summary>Gets the certificate's subject distinguished name.</summary>
    public required string Subject { get; init; }

    /// <summary>Gets the certificate's issuer distinguished name.</summary>
    public required string Issuer { get; init; }

    /// <summary>Gets the certificate's serial number, as a hexadecimal string.</summary>
    public required string SerialNumber { get; init; }

    /// <summary>Gets the certificate's SHA-1 thumbprint (fingerprint), as a hexadecimal string.</summary>
    public required string Thumbprint { get; init; }
}
