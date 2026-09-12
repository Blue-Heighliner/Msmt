# Msmt

[![NuGet](https://img.shields.io/nuget/v/BlueHeighliner.Msmt.svg?label=NuGet)](https://www.nuget.org/packages/BlueHeighliner.Msmt)
[![License: MIT](https://img.shields.io/github/license/Blue-Heighliner/Msmt.svg)](LICENSE)
[![Build](https://github.com/Blue-Heighliner/Msmt/actions/workflows/build.yml/badge.svg)](https://github.com/Blue-Heighliner/Msmt/actions/workflows/build.yml)
[![Coverage](https://raw.githubusercontent.com/Blue-Heighliner/Msmt/main/.github/badges/badge_linecoverage.svg)](https://github.com/Blue-Heighliner/Msmt/actions/workflows/build.yml)

A C# implementation of the Mercury Secure Message Transport (MSMT) standard — an open,
standards-based interface for secure message transport over IP networks, defined by MITRE's
*Mercury Secure Message Transport Interface Control Document (ICD)*. MSMT is a fixed, narrowly
pinned configuration of TLS 1.3 paired with a thin, standardized message-framing API; this library
implements that interface end to end (client, server, and peer-to-peer wrapper) on top of
BouncyCastle's TLS engine.

## Installing

```sh
dotnet add package BlueHeighliner.Msmt
```

## Getting started

```csharp
using BlueHeighliner.Msmt;

MsmtCredentials credentials = MsmtCredentials.FromPemFiles("identity.pem", "identity.key", "ca.pem");

// RequireFullyQualifiedHostname is disabled here only because this example connects by loopback IP
// address rather than a real DNS hostname; leave it enabled (the default) whenever a hostname is used.
IMsmtPeer peer = new MsmtPeerFactory().Create(new MsmtOptions { Credentials = credentials, RequireFullyQualifiedHostname = false });

peer.Received.Subscribe(args =>
{
    using (args.Payload)
    {
        Console.WriteLine(Encoding.UTF8.GetString(args.Payload.Memory.Span));
    }
});

peer.StartListener(port: 5000);
peer.Send(new MsmtTarget { Host = "127.0.0.1", Port = 5000 }, "hello"u8.ToArray());
```

## Documentation

| File | Covers |
|------|--------|
| [Docs/Api.md](Docs/Api.md) | The public API's design and flow, built around `IMsmtPeer` |
| [Docs/Usage.md](Docs/Usage.md) | Usage examples for common scenarios |
| [Docs/Architecture.md](Docs/Architecture.md) | The high-level design decisions behind the library |
| [Docs/Components/](Docs/Components/) | Design/implementation detail for individual complex components, one file each |
| [Docs/ICD.md](Docs/ICD.md) | Full Markdown transcription of the Mercury Secure Message Transport Interface Control Document (v1.2) |
| [Docs/Project.md](Docs/Project.md) | This repository's own tooling and workflow: `Scripts/`, publishing, CI |
