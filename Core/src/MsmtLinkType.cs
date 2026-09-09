namespace BlueHeighliner.Msmt;

/// <summary>Distinguishes the two directions an <see cref="IMsmtLink"/> can represent within an <see cref="IMsmtConnection"/>.</summary>
public enum MsmtLinkType
{
    /// <summary>An outgoing link this peer created on demand to send messages to the remote peer.</summary>
    Sender,

    /// <summary>An incoming link this peer's receiver accepted, over which the remote peer sends it messages.</summary>
    Receiver,
}
