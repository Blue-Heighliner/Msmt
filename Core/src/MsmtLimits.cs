namespace BlueHeighliner.Msmt;

/// <summary>
/// The fixed limits the MSMT wire format imposes on a single message.
/// </summary>
public static class MsmtLimits
{
    /// <summary>
    /// Gets the largest payload, in bytes, a single MSMT message or acknowledgement may carry. A peer
    /// rejects any message whose header declares a larger length as malformed, so <see
    /// cref="IMsmtPeer.Send"/>, <see cref="IMsmtPeer.Request"/>, and <see cref="IMsmtResponder"/>'s
    /// accept/reject overloads all reject an oversized payload up front rather than transmitting one the
    /// remote peer could never accept. Bundle application-level messages to stay within it, per the ICD's
    /// guidance on choosing a maximum bundle size.
    /// </summary>
    public static int MaxPayloadLength { get; } = 0xFFFFFF;
}
