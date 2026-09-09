namespace BlueHeighliner.Msmt;

/// <summary>
/// Identifies a remote MSMT peer by address and port.
/// </summary>
public sealed record MsmtTarget
{
    /// <summary>Gets the remote peer's IP address or DNS hostname.</summary>
    public required string Host { get; init; }

    /// <summary>Gets the remote peer's port.</summary>
    public required int Port { get; init; }

    /// <summary>Converts <paramref name="target"/> to an <see cref="MsmtNameTarget"/>, using its <see cref="Host"/> as the TLS "server_name" (SNI) value.</summary>
    /// <param name="target">The target to convert.</param>
    public static implicit operator MsmtNameTarget(MsmtTarget target) =>
        new() { Host = target.Host, Port = target.Port, ServerName = target.Host };
}
