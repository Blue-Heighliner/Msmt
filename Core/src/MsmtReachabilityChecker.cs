namespace BlueHeighliner.Msmt;

/// <summary>
/// Checks whether a remote MSMT peer is reachable and correctly configured, per the ICD's reachability
/// check (section 4.3), without generating real message traffic or invoking the remote peer's application
/// logic.
/// </summary>
public interface IMsmtReachabilityChecker
{
    /// <summary>
    /// Establishes a connection to <paramref name="target"/> and exchanges a specially-flagged test message
    /// that is never delivered to the remote peer's application logic, then closes the connection.
    /// </summary>
    /// <param name="target">The remote peer to check.</param>
    /// <param name="options">The credentials and connection-behavior settings to check with.</param>
    /// <param name="cancellation">Cancels the check, interrupting a handshake or read already in progress.</param>
    /// <returns><see langword="true"/> if the remote peer responded successfully.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A timeout in <paramref name="options"/> is not positive.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellation"/> was cancelled.</exception>
    /// <exception cref="TimeoutException">The remote peer did not complete the handshake, or acknowledge the check, within the configured timeouts.</exception>
    Task<bool> Reach(MsmtNameTarget target, MsmtOptions options, CancellationToken cancellation = default);
}

/// <inheritdoc cref="IMsmtReachabilityChecker" />
/// <remarks>
/// Stateless: each <see cref="Reach"/> call builds its own connector from the options passed to it, then
/// opens, uses, and closes a connection of its own, so one checker may be reused freely and concurrently.
/// Carries no configuration of its own, so unlike <see cref="IMsmtMessagePeer"/>/<see
/// cref="IMsmtSessionPeer"/> it needs no factory - construct it directly, with its parameterless
/// constructor, or resolve <see cref="IMsmtReachabilityChecker"/> through dependency injection when using
/// <see cref="MsmtServiceCollectionExtensions.AddMsmt"/>.
/// </remarks>
public sealed class MsmtReachabilityChecker : IMsmtReachabilityChecker
{
    /// <inheritdoc />
    public async Task<bool> Reach(MsmtNameTarget target, MsmtOptions options, CancellationToken cancellation = default)
    {
        options.Validate();
        IMsmtConnector connector = new MsmtConnector(options, requireFullyQualifiedHostname: false);

        MsmtConnection connection = await connector.DialAndWait(target, new MsmtConnectionSettings
        {
            Role = MsmtConnectionRole.Initiator,
            Mode = MsmtConnectionMode.Requesting,
            HandshakeTimeout = options.HandshakeTimeout,
            StallTimeout = options.StallTimeout,
            ResponseTimeout = options.ResponseTimeout,
            ProcessSends = false,
        }, tracker: null, cancellation);

        try
        {
            return await connection.Ping(cancellation);
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }
}
