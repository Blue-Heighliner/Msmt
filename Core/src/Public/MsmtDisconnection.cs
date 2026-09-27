namespace BlueHeighliner.Msmt;

/// <summary>Event data for a connection that closed.</summary>
public sealed record MsmtDisconnection
{
    /// <summary>Gets the connection that closed.</summary>
    public required IMsmtConnection Connection { get; init; }

    /// <summary>
    /// Gets the exception that caused the connection to close, such as a <see cref="TimeoutException"/> for a
    /// stalled or unused connection, or <see langword="null"/> if it closed normally, whether at either
    /// side's request or because the remote peer closed it.
    /// </summary>
    public Exception? Exception { get; init; }
}
