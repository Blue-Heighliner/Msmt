namespace BlueHeighliner.Msmt;

/// <summary>Configuration for an <see cref="IMsmtSessionPeer"/>.</summary>
public sealed record MsmtSessionPeerOptions : MsmtOptions
{
    /// <summary>Gets the cap this peer applies to a connecting client's proposed session lifetime: the agreed lifetime is the lesser of the two. Defaults to 10 minutes.</summary>
    public TimeSpan MaximumSessionLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Gets a value indicating whether this peer's listener rejects a connecting client's "server_name"
    /// (SNI) value unless it is a fully qualified DNS hostname, per the ICD, rather than merely a
    /// syntactically valid one (which also accepts IP address literals). Defaults to <see
    /// langword="true"/>; disable for testing or environments without DNS, where connecting by address is
    /// otherwise the only option.
    /// </summary>
    public bool RequireFullyQualifiedHostname { get; init; } = true;

    /// <summary>
    /// Gets the maximum connection lifetime this peer proposes when opening a connection via <see
    /// cref="IMsmtSessionPeer.Connect"/>. The remote peer may agree to less. Defaults to 10 minutes,
    /// comfortably above the 3 to 5 minute keep-alive interval, so an idle session has room to be kept
    /// alive rather than simply expiring first.
    /// </summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets the shortest time a connection this peer opened may sit without any message before a keep-alive is sent. Each connection picks a random interval between this and <see cref="KeepAliveMaxInterval"/>, per the ICD.</summary>
    public TimeSpan KeepAliveMinInterval { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Gets the longest time a connection this peer opened may sit without any message before a keep-alive is sent. Must not be less than <see cref="KeepAliveMinInterval"/>.</summary>
    public TimeSpan KeepAliveMaxInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    internal override void Validate()
    {
        base.Validate();

        if (MaximumSessionLifetime < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumSessionLifetime), MaximumSessionLifetime, "MaximumSessionLifetime must be at least one second.");
        }

        foreach ((string name, TimeSpan value) in new (string, TimeSpan)[]
        {
            (nameof(SessionLifetime), SessionLifetime),
            (nameof(KeepAliveMinInterval), KeepAliveMinInterval),
            (nameof(KeepAliveMaxInterval), KeepAliveMaxInterval),
        })
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(name, value, $"{name} must be positive.");
            }
        }

        if (KeepAliveMinInterval > KeepAliveMaxInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(KeepAliveMinInterval), KeepAliveMinInterval, "KeepAliveMinInterval must not exceed KeepAliveMaxInterval.");
        }
    }
}
