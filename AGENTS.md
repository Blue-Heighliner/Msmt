# AGENTS.md

Guidance for AI agents working in this repository.

## Maintaining This File

- Whenever a rule is added, removed, or changed in this file, re-review the whole file afterward and tighten it: merge overlapping or closely related bullets, cut redundant phrasing, and otherwise keep it as condensed as possible without dropping any distinct rule, example, or the reasoning behind a non-obvious rule.

## Code Conventions

### Language and Framework
- .NET, C#. Use the latest language features without hesitation.
- File-scoped namespaces everywhere.
- Each project has a single `Using.cs` file containing all `global using` directives for that project. Do not place `using` directives in individual files.
- Each project restores with a committed `packages.lock.json` (`RestorePackagesWithLockFile`). After changing a `PackageReference`, run `dotnet restore` and commit the updated lock file - CI restores in locked mode and fails if it's out of sync.

### Style
- `.editorconfig` mechanically enforces the subset of these rules a formatter can check (braces, file-scoped namespaces, no `var`, private-field/public-member casing, etc.); CI fails a PR that doesn't pass `dotnet format --verify-no-changes`. Run that command (or `dotnet format`, to apply fixes) locally before committing. Rules with no mechanical check (member ordering, `sealed`-by-default, interface co-location, async naming, XML doc coverage on `internal` members) still apply and rely on review.
- No comments unless the why is non-obvious — a hidden constraint, a platform quirk, a non-obvious invariant. Never comment what the code obviously does. No multi-line comment blocks, and no comments used to designate sections of members (e.g. `// ── Section ──` dividers) — order members by the rule below instead.
- All types and members (`public` and `internal`) require full XML documentation comments (`<summary>`, `<param>`, `<returns>`, `<exception>`, etc. as applicable). Use `<inheritdoc />` on members that implement a documented interface without adding meaningful additional documentation.
- `sealed` on all classes that are not designed for inheritance.
- Prefer `record` types with `required init` properties for DTOs and model types.
- `internal` by default; only `public` what a consuming application genuinely needs.
- One type per file, with these exceptions: an interface and its implementing class are co-located in one file named after the class (e.g. `IThing` and `Thing` both live in `Thing.cs`); extension classes are co-located with the class they extend (e.g. `ThingExtensions` also lives in `Thing.cs`); a standalone interface with no co-located implementation is named after its concept without the `I` prefix (e.g. `IOther` lives in `Other.cs`).
- Member ordering: group static members at the top of a type, then instance members below them. Within each group, order members by kind: constructors, fields, events, properties, operators, methods. Applies to interfaces too (events, then properties, then methods) — an interface's members are not exempt just because it has no constructors or fields.
- Always use braces for blocks — never an implicit one-line `if`/`else`/`for`/`foreach`/`while`/etc. Always write `if (...) { ... }`, never `if (...) ...`.
- Prefer expression-bodied members over full block bodies when possible and practical (e.g. `void Do() => Action();`). For methods, when the signature and expression body don't fit on one line, wrap with `=>` indented on its own line beneath the signature, not trailing at the end of the signature line. For properties, the `=>` always stays on the same line as the signature (e.g. `public int Foo => value;`), even if the expression itself then needs to wrap onto following lines — never move the `=>` itself down to its own line as with methods.
- Do not define a private static field solely to back an instance property that always returns the same value. Initialize the instance property directly instead (e.g. `public IReadOnlyList<X> Foo { get; } = [...];`) rather than adding a separate `private static readonly` field just to hold that value.
- When a property returns a reference-type value (e.g. a list, array, dictionary, or other object), prefer storing that value in the property's own backing field, computed once, rather than an expression body that constructs a new value on every access. Do `IList<int> Numbers { get; } = [5, 2, 3];`, not `IList<int> Numbers => [5, 2, 3];`.
- For a fixed scalar value, prefer a `static` read-only property (`public static string Foo { get; } = "value";`) over a `const` field — reserve `const` for a value declared locally inside a method body, a different, unrelated idiom.
- Prefer private instance fields over private static fields, even when the value is the same for every instance, and prefer instance methods over `static` ones even when a method touches no instance state. Reserve `static` for cases that genuinely require it (extension methods, backing a static member, a genuine global access point like a service locator).
- Do not prefix private field names with an underscore. Use plain camelCase for private fields (e.g. `messageQueue`); public/internal properties use PascalCase (e.g. `MessageQueue`) — the casing itself is what distinguishes a private field from a public property, not a leading underscore.
- For private fields holding plain internal data (not an injected DI dependency), declare the field with its concrete/plain class type rather than an interface (e.g. `private readonly Dictionary<string, MsmtConnection> connections = new();`, not `IReadOnlyDictionary<string, MsmtConnection>`; `private readonly List<string> targets = [...];`, not `IReadOnlyList<string>`). This does not apply to DI-injected constructor/property dependencies, which continue to use their interface type per the DI convention.
- Prefer primary constructors when possible.

### Patterns
- Documentation stays in sync with code: review and update the relevant `Docs/` file whenever you change wire-format, protocol, or public API behavior. Keep docs as brief as possible without sacrificing essential details — a concise statement of what/why beats exhaustive narration. Docs describe only current behavior — update the relevant section in place rather than layering "used to be X, now Y" notes, since git history is where past behavior belongs.
- Interface co-location: give every non-DTO class a corresponding `IThing` interface declared in the same file (`Thing.cs`); constructor and property injection always uses the interface type, never the concrete class.
- Async all the way: write all I/O as async. Avoid `Task.Result` and `.GetAwaiter().GetResult()` except where a synchronization context deadlock is explicitly being avoided at a top-level entry point. Do not append `Async` to method names — name methods by what they do, not how they do it (`Send`, not `SendAsync`). Name `CancellationToken` parameters `cancellation` (not `ct` or `cancellationToken`); framework-required overrides are the only exception.
- Thread safety: use `SemaphoreSlim(1,1)` for async-compatible locking, `ConcurrentDictionary` for shared maps, `lock` for short synchronous critical sections.
- Byte spans over arrays: use `ReadOnlyMemory<byte>` / `ReadOnlySpan<byte>` for payload and data-chunk parameters and return types at method boundaries; use `Memory<byte>` when the callee needs to write. Reserve `byte[]` for internal read buffers allocated with `new byte[n]` and for interop with APIs that require it.
- No `var`: always declare the explicit type on local variables. Use C# 9+ target-typed `new()` to avoid repetition when the type is already on the left-hand side (e.g. `MsmtClient client = new();`). For tuple deconstructions write the types inline: `(string id, bool ok) = GetResult()`.

### Tests
- Put new unit tests (no real network I/O) in `Tests/src/Unit/`; integration tests (real `MsmtPeer`/`MsmtClient`/`MsmtServer` exchanges over TCP/TLS) in `Tests/src/Integration/`. Put shared test infrastructure (e.g. `TestMsmtCertificates`) directly under `Tests/src/`. Use xUnit; mock dependencies with Moq.
- Do not add `#if DEBUG` guards. All features must work in Release configuration.

## Packaging

- Only `Core/Core.csproj` carries NuGet package metadata (`PackageId`, description, tags, etc.); never add it to `Tests.csproj`, which must stay unpacked (`IsPackable false`).
- Never add a `<Version>` to `Core/Core.csproj` — the only way to release is dispatching the `csharp.yml` workflow (`workflow_dispatch`, restricted to repo Admins/org Owners) with a plain `Major.Minor.Patch[-prerelease]` version (no leading `v`, e.g. `1.2.3`; `Tasks/Publish.task` does this interactively). That single trigger creates the tag, the GitHub Release, and publishes the package - pushing a tag directly does not.
