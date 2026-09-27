namespace BlueHeighliner.Msmt.Internal;

/// <summary>Something an owner may disconnect to enforce its idle time and connection count limits, provided it is not in the middle of a message.</summary>
internal interface IMsmtEvictable
{
    /// <summary>Gets a value indicating whether nothing is queued, in flight, or being handled, so evicting it interrupts nothing.</summary>
    bool IsIdle { get; }

    /// <summary>Gets the last time application traffic finished, in either direction.</summary>
    DateTime LastActivityUtc { get; }
}
