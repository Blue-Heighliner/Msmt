namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// A <see cref="TlsClientProtocol"/> that exposes the ability to trigger a TLS 1.3 key update on
/// demand, which the base BouncyCastle API only ever performs automatically and does not expose
/// publicly, so that <see cref="MsmtOperationMode.MessageWithRekeying"/> can request one between
/// messages.
/// </summary>
internal sealed class RekeyableTlsClientProtocol(Stream stream) : TlsClientProtocol(stream)
{
    /// <summary>Sends a TLS 1.3 key update, also requesting the peer update its own sending keys.</summary>
    public void Rekey() => Send13KeyUpdate(true);
}
