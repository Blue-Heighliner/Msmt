# Msmt

[![NuGet](https://img.shields.io/nuget/v/BlueHeighliner.Msmt.svg?label=NuGet)](https://www.nuget.org/packages/BlueHeighliner.Msmt)
[![License: MIT](https://img.shields.io/github/license/Blue-Heighliner/Msmt.svg)](LICENSE)
[![Build](https://github.com/Blue-Heighliner/Msmt/actions/workflows/build.yml/badge.svg)](https://github.com/Blue-Heighliner/Msmt/actions/workflows/build.yml)
[![Coverage](https://raw.githubusercontent.com/Blue-Heighliner/Msmt/main/.github/badges/badge_linecoverage.svg)](https://github.com/Blue-Heighliner/Msmt/actions/workflows/build.yml)

A C# implementation of the Mercury Secure Message Transport (MSMT) standard - an open,
standards-based interface for secure message transport over IP networks, defined by MITRE's
*Mercury Secure Message Transport Interface Control Document (ICD)*. MSMT is a fixed, narrowly
pinned configuration of TLS 1.3 paired with a thin, standardized message-framing API; this library
implements that interface end to end on top of BouncyCastle's TLS engine.

## Installing

```sh
dotnet add package BlueHeighliner.Msmt
```

## Getting started

A peer sends messages directly to targets and receives them directly from targets, in message mode or
message-with-rekeying mode:

```csharp
using BlueHeighliner.Msmt;

MsmtCredentials credentials = MsmtCredentials.FromPemFiles("identity.pem", "identity.key", "ca.pem");

// RequireFullyQualifiedHostname is disabled here only because this example connects by loopback IP
// address rather than a real DNS hostname; leave it enabled (the default) whenever a hostname is used.
IMsmtMessagePeer peer = new IMsmtMessagePeer.Factory().Create(new MsmtMessagePeerOptions { Credentials = credentials, RequireFullyQualifiedHostname = false });

// The peer disposes payload once this handler's returned ValueTask completes; copy anything needed beyond that.
peer.Receiver = (source, identity, payload, isResponseRequested) =>
{
    Console.WriteLine(Encoding.UTF8.GetString(payload.Span));
    return default; // null: this message never requested an acknowledgement, so none is sent
};

peer.StartListener(port: 5000);
peer.Send(new MsmtTarget { Host = "127.0.0.1", Port = 5000 }, "hello"u8.ToArray());
```

A session peer listens for and opens session connections, which are bidirectional: both sides send and
receive over the same connection object.

```csharp
IMsmtSessionPeer listening = new IMsmtSessionPeer.Factory().Create(new MsmtSessionPeerOptions { Credentials = credentials, RequireFullyQualifiedHostname = false });
listening.Receiver = (connection, payload, isResponseRequested) =>
{
    Console.WriteLine($"got {payload.Length} bytes");
    return default;
};
listening.Connected.Subscribe(connection => connection.Send("welcome"u8.ToArray())); // the listening side can send too
listening.StartListener(port: 5000);

IMsmtSessionPeer connecting = new IMsmtSessionPeer.Factory().Create(new MsmtSessionPeerOptions { Credentials = credentials });
connecting.Receiver = (connection, payload, isResponseRequested) =>
{
    Console.WriteLine("connecting side got a message");
    return default;
};
IMsmtConnection connection = connecting.Connect(new MsmtNameTarget { Host = "127.0.0.1", Port = 5000, ServerName = "127.0.0.1" });
await connection.Wait(); // still Connecting until this resolves
MsmtResponse response = await connection.Request("hello"u8.ToArray());
```

## Documentation

| File | Covers |
|------|--------|
| [Docs/Api.md](Docs/Api.md) | The public API's design and flow: `IMsmtMessagePeer` and `IMsmtSessionPeer` with its connections |
| [Docs/Usage.md](Docs/Usage.md) | Usage examples for common scenarios |
| [Docs/Architecture.md](Docs/Architecture.md) | The high-level design decisions behind the library |
| [Docs/Components/](Docs/Components/) | Design/implementation detail for individual complex components, one file each |
| [Docs/ICD.md](Docs/ICD.md) | Full Markdown transcription of the Mercury Secure Message Transport Interface Control Document (v1.2) |
| [Docs/Project.md](Docs/Project.md) | This repository's own tooling and workflow: `Scripts/`, publishing, CI |
