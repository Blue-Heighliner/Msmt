# TLS layer

`MsmtTlsClient`/`MsmtTlsServer`/`RekeyableTlsClientProtocol`/`MsmtBcCryptography`/`MsmtTlsChannel` - the
TLS 1.3 layer, built directly on `Org.BouncyCastle.Tls`.

`MsmtTlsClient`/`MsmtTlsServer` extend BouncyCastle's `DefaultTlsClient`/`DefaultTlsServer`, pinning
`GetSupportedVersions()` to TLS 1.3 only and `GetSupportedCipherSuites()` to exactly
`TLS_CHACHA20_POLY1305_SHA256` and `TLS_AES_256_GCM_SHA384`, in that order, per the ICD. `MsmtTlsClient`
always presents a `ServerName` (SNI) extension from `MsmtNameTarget.ServerName`, and its nested
`Authentication` verifies the server's certificate (`MsmtBcCryptography.IsTrusted`) and matching server
name (`MatchesServerName`) before recording `ServerIdentity`, and supplies this client's own credentials
when the server requests mutual authentication. `MsmtTlsServer.ProcessClientExtensions` rejects a handshake
missing a server name extension or presenting an unacceptable one (see `RequireFullyQualifiedHostname`,
declared per endpoint type, and `IsValidHostname`), and `NotifyClientCertificate`
verifies the client's certificate the same way before recording `ClientIdentity`.

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

## The channel

`MsmtTlsChannel` runs a TLS connection over a socket without ever blocking a thread. BouncyCastle's
stream-based API can't be used here: a write fails while a read is waiting on the same stream, so a
connection could never be read and written at once, which bidirectional sessions require; and a blocked
read or handshake ignores cancellation, so the only way to interrupt one was to close the socket. The
channel drives BouncyCastle's non-blocking protocol API instead (`OfferInput`, `ReadInput`,
`WriteApplicationData`, `ReadOutput`).

The handshake is a short loop: send whatever the protocol has produced, and while it is still handshaking,
receive and offer more input. After it, two loops run. The read loop receives from the socket, offers the
bytes to the protocol under a lock, and collects the decrypted application data and anything the protocol
produced in response (a key update reply, an alert). The write loop is the only thing that sends: every
producer, the read loop and `Write`, appends what it drained from the protocol to one ordered queue while
still holding the lock, so bytes reach the wire in exactly the order the protocol produced them, which TLS
record sequencing requires.

Two bounds keep a slow peer from costing unbounded memory or time. Decrypted data waits in a bounded queue
(about 1 MiB); once it is full the read loop stops reading the socket and TCP flow control pushes back on
the sender. `Write` splits data into record-sized slices and takes a credit for each, returned as the write
loop sends it, so a writer waits when the socket is behind. Each chunk sent is also timed: if sending
makes no progress for the stall timeout, the channel aborts with a `TimeoutException`, and every later read
and write reports it.

Cancellation is ordinary: cancelling a `Read` or `Write` abandons that call and leaves the channel usable.
`Abort` closes the socket at once, discarding what is queued; `Close` sends a TLS close notification, lets
the queue go out, then closes. A read returns `0` when the remote side has closed the connection, and
throws when the channel was aborted with a reason.

`RekeyableTlsClientProtocol` extends `TlsClientProtocol` purely to expose `Send13KeyUpdate` (otherwise
`protected`) as a public `Rekey()` method, since message-with-rekeying needs to trigger a key update
between messages without a full handshake.
