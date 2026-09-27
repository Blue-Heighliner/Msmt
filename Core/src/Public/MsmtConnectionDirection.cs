namespace BlueHeighliner.Msmt;

/// <summary>Which side opened an <see cref="IMsmtConnection"/>.</summary>
public enum MsmtConnectionDirection
{
    /// <summary>The remote peer opened this connection; the local <see cref="IMsmtSessionPeer"/> accepted it.</summary>
    Incoming,

    /// <summary>The local <see cref="IMsmtSessionPeer"/> opened this connection via <see cref="IMsmtSessionPeer.Connect"/>.</summary>
    Outgoing,
}
