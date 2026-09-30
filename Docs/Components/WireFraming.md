# Wire framing

The wire format shared by every connection: the fixed 10-byte header (`MsmtHeader`), its flags
(`MsmtMessageFlags`), and how a received header is told to be a new message or the acknowledgement of one
already sent (`MsmtConnection`).

Every message and acknowledgement is a fixed 10-byte `MsmtHeader` (1-byte version, 1 reserved byte, a
16-bit `MsmtMessageFlags`, a 16-bit random message ID, and a 32-bit big-endian length) followed by an
opaque payload of that length. `MsmtHeader.SupportedVersion` is `3` (MSMT v1.2, with the Message ID field);
`IsWellFormed()` rejects any other version, any bit outside `DefinedFlags`, or a length beyond `MaxLength`
(`0xFFFFFF`, exposed publicly as `MsmtLimits.MaxPayloadLength`), which `MsmtProtocol.ValidatePayloadLength`
also enforces outbound - at `Send`/`Request` and `IMsmtResponder.Accept`/`.Reject`, before a payload is
queued or written - so an oversized payload fails fast with an `ArgumentOutOfRangeException` instead of
being transmitted and then rejected as malformed mid-connection. `Acknowledges` matches a response header back to
its request by version and message ID. `MsmtMessageFlags` is a `[Flags]` enum whose bits are combined for
higher-level outcomes - e.g. `SessionModeAccepted` is `SessionModeNegotiation | MessageSuccess`.

A payload is read straight into a buffer rented from `MemoryPool<byte>.Shared`, sliced to the exact length
with the public `Slice` extension since a pool may return a larger buffer than requested, so a received
message never costs an extra allocation beyond the pool's own.

## Requests and acknowledgements share a header

The ICD gives a message and its acknowledgement the same header, and its "ACK requested" and "ACK
acknowledged" share one flag bit, so the flags cannot say which a frame is once both sides may send. A
connection therefore keeps the request it has in flight and treats a frame as its acknowledgement only if
its message ID matches; any other frame is a new request from the remote side. Only one request per
direction is ever in flight, so one ID is all there is to compare.

Message IDs are random, but the low bit is fixed by role: the side that opened the connection uses even
IDs and the side that accepted it odd ones, so the two sides can never pick the same ID for requests in
flight at once. That costs one bit of randomness, which the ICD only uses to correlate messages and log them.

The three unused flag bits must stay zero, so nothing here adds a direction or capability marker to the
wire. A peer that has not been written to expect it would misread a frame it did not ask for, which is why
only session connections, where both ends are this library, are bidirectional. A reachability check is
echoed by whichever side receives it, and a message whose header is malformed is answered with the invalid
preamble flag before the connection is closed.
