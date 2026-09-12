# Wire framing

Shared wire-level helpers used by both `MsmtClient` and `MsmtServer`: the fixed 10-byte header
(`MsmtHeader`), its flags (`MsmtMessageFlags`), and pooled-buffer read/write helpers (`MsmtProtocol`).

Every message and acknowledgement is a fixed 10-byte `MsmtHeader` (1-byte version, 1 reserved byte, a
16-bit `MsmtMessageFlags`, a 16-bit random message ID, and a 32-bit big-endian length) followed by an
opaque payload of that length. `MsmtHeader.SupportedVersion` is `3` (MSMT v1.2, with the Message ID field);
`IsWellFormed()` rejects any other version, any bit outside `DefinedFlags`, or a length beyond `MaxLength`
(`0xFFFFFF`, exposed publicly as `MsmtLimits.MaxPayloadLength`), which `MsmtProtocol.ValidatePayloadLength`
also enforces outbound - at `Send`/`Request` and `MsmtResponder.Decide`, before a payload is queued or
written - so an oversized payload fails fast with an `ArgumentOutOfRangeException` instead of being
transmitted and then rejected as malformed mid-connection. `Acknowledges` matches a response header back to
its request by version and message ID. `MsmtMessageFlags` is a `[Flags]` enum whose bits are combined for
higher-level outcomes - e.g. `SessionModeAccepted` is `SessionModeNegotiation | MessageSuccess`.

`MsmtProtocol.ReadPooled` reads a payload directly into a buffer rented from `MemoryPool<byte>.Shared`
(sliced to the exact requested length via `SlicedMemoryOwner`, since a pool may return a larger buffer than
requested), so a received message never costs an extra allocation beyond the pool's own; `ReadExact` is the
underlying loop-until-filled primitive both `ReadPooled` and raw header reads use.

`MsmtProtocol` also exposes `MaxTimerDuration`/`ClampToMaxTimerDuration`, used by both `MsmtClient` and
`MsmtServerConnection` to chain arbitrarily long idle-timeout waits around `Task.Delay`'s roughly 49.7-day
ceiling.
