# TLS layer

`MsmtTlsClient`/`MsmtTlsServer`/`RekeyableTlsClientProtocol`/`MsmtBcCryptography` - the TLS 1.3 layer, built
directly on `Org.BouncyCastle.Tls`.

`MsmtTlsClient`/`MsmtTlsServer` extend BouncyCastle's `DefaultTlsClient`/`DefaultTlsServer`, pinning
`GetSupportedVersions()` to TLS 1.3 only and `GetSupportedCipherSuites()` to exactly
`TLS_CHACHA20_POLY1305_SHA256` and `TLS_AES_256_GCM_SHA384`, in that order, per the ICD. `MsmtTlsClient`
always presents a `ServerName` (SNI) extension from `MsmtNameTarget.ServerName`, and its nested
`Authentication` verifies the server's certificate (`MsmtBcCryptography.IsTrusted`) and matching server
name (`MatchesServerName`) before recording `ServerIdentity`, and supplies this client's own credentials
when the server requests mutual authentication. `MsmtTlsServer.ProcessClientExtensions` rejects a handshake
missing a server name extension or presenting an unacceptable one (see
`MsmtHostOptions.RequireFullyQualifiedHostname`/`IsValidHostname`), and `NotifyClientCertificate` verifies
the client's certificate the same way before recording `ClientIdentity`.

`MsmtBcCryptography` bridges .NET's `X509Certificate2`-based `MsmtCredentials` to BouncyCastle's own
certificate/key types (`ToBcIdentity`), and implements the trust check both sides apply to the peer's
presented chain (`IsTrusted`): built against a `CustomRootTrust` `X509Chain` seeded with the configured
trusted authorities, requiring an RSA key of at least 2048 bits (`MinimumRsaKeySizeBits`), with revocation
checked offline (against any already-cached CRL, never fetched live) and a missing/unavailable revocation
source tolerated rather than treated as a failure. Only RSA identity certificates are supported -
`ToBcIdentity` throws `NotSupportedException` for anything else - and `SelectSignatureAlgorithm` limits
signature algorithm negotiation to the three RSA-PSS schemes (`rsa_pss_rsae_sha256/384/512`) BouncyCastle's
engine can sign with directly. `MatchesServerName` implements RFC 6125 matching against a certificate's
Subject Alternative Name entries (or Common Name, if no SAN extension is present), including a single
leftmost wildcard DNS label.

`RekeyableTlsClientProtocol` extends `TlsClientProtocol` purely to expose `Send13KeyUpdate` (otherwise
`protected`) as a public `Rekey()` method, since `MessageWithRekeying` needs to trigger a key update
between messages without a full handshake.
