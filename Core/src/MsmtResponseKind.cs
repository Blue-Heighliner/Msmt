namespace BlueHeighliner.Msmt;

/// <summary>
/// The decision an <see cref="IMsmtResponder"/> has made about the message it is responding to.
/// </summary>
public enum MsmtResponseKind
{
    /// <summary>Neither <see cref="IMsmtResponder.Accept()"/> nor <see cref="IMsmtResponder.Reject()"/> has been called yet.</summary>
    None,

    /// <summary>The message was accepted; a positive acknowledgement is sent.</summary>
    Accept,

    /// <summary>The message was rejected; a negative acknowledgement is sent.</summary>
    Reject,
}
