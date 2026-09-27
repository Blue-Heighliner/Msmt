namespace BlueHeighliner.Msmt;

/// <summary>
/// Identifies a remote MSMT peer by address, port, and the TLS server name to present via SNI and
/// validate against the peer's certificate.
/// </summary>
/// <remarks>
/// Does not inherit from <see cref="MsmtTarget"/>: C# forbids a user-defined conversion between a base and
/// derived type, so keeping them independent record types is what makes <see cref="MsmtTarget"/>'s implicit
/// conversion to this type possible.
/// </remarks>
public sealed record MsmtNameTarget
{
    /// <summary>Gets the remote peer's IP address or DNS hostname.</summary>
    public required string Host { get; init; }

    /// <summary>Gets the remote peer's port.</summary>
    public required int Port { get; init; }

    /// <summary>Gets the fully qualified hostname presented in the TLS "server_name" (SNI) extension and validated against the remote peer's certificate.</summary>
    public required string ServerName { get; init; }
}
