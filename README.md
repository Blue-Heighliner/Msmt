# Msmt

[![NuGet](https://img.shields.io/nuget/v/BlueHeighliner.Msmt.svg?label=NuGet)](https://www.nuget.org/packages/BlueHeighliner.Msmt)
[![License: MIT](https://img.shields.io/github/license/Blue-Heighliner/Msmt.svg)](LICENSE)
[![C#](https://github.com/Blue-Heighliner/Msmt/actions/workflows/csharp.yml/badge.svg)](https://github.com/Blue-Heighliner/Msmt/actions/workflows/csharp.yml)
[![codecov](https://codecov.io/gh/Blue-Heighliner/Msmt/branch/main/graph/badge.svg)](https://codecov.io/gh/Blue-Heighliner/Msmt)

A C# implementation of the Mercury Secure Message Transport (MSMT) standard — an open,
standards-based interface for secure message transport over IP networks, defined by MITRE's
*Mercury Secure Message Transport Interface Control Document (ICD)*. MSMT is a fixed, narrowly
pinned configuration of TLS 1.3 paired with a thin, standardized message-framing API; this library
implements that interface end to end (client, server, and peer-to-peer wrapper) on top of
BouncyCastle's TLS engine.

## Projects

| Project | Description |
|---------|-------------|
| **Core** | The MSMT client, server, and peer-to-peer library. |
| **Tests** | xUnit tests for Core. |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Installing

```sh
dotnet add package BlueHeighliner.Msmt
```

## Building

```sh
dotnet build
```

## Testing

```sh
dotnet test
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

See [Docs/Api.md](Docs/Api.md) for the public API's design and flow, and [Docs/Usage.md](Docs/Usage.md)
for more usage examples.

## Documentation

| File | Covers |
|------|--------|
| [Docs/Api.md](Docs/Api.md) | The public API's design and flow, built around `IMsmtPeer` |
| [Docs/Usage.md](Docs/Usage.md) | Usage examples for common scenarios |
| [Docs/Architecture.md](Docs/Architecture.md) | The high-level design decisions behind the library |
| [Docs/Implementation.md](Docs/Implementation.md) | How `MsmtPeer`'s real implementation works internally: components, wire framing, TLS layer, concurrency model |
| [Docs/ICD.md](Docs/ICD.md) | Full Markdown transcription of the Mercury Secure Message Transport Interface Control Document (v1.2) |

Public and internal types are also fully documented with XML doc comments throughout the source.

## License

[MIT](LICENSE)
