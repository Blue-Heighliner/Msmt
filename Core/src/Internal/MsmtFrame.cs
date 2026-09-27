namespace BlueHeighliner.Msmt.Internal;

/// <summary>A received MSMT message: its header and its pooled payload, which the holder must dispose.</summary>
/// <param name="Header">The message header.</param>
/// <param name="Payload">The message payload, sliced to exactly the header's length.</param>
internal readonly record struct MsmtFrame(MsmtHeader Header, IMemoryOwner<byte> Payload);
