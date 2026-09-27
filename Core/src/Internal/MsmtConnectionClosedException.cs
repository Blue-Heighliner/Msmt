namespace BlueHeighliner.Msmt.Internal;

/// <summary>Thrown when a connection is used after it has closed. Raised before anything is written, so the message can safely be sent again over a new connection.</summary>
internal sealed class MsmtConnectionClosedException() : IOException("The MSMT connection is closed.");
