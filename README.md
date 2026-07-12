# PlayGroundSharp

PlayGroundSharp is a Windows C# interactive console inspired by Chrome DevTools Console. Submit one expression at a time while variables, methods, types, usings and references remain available in later submissions.

> **Security:** PlayGroundSharp is not a sandbox. Submitted C# and packages run with the current Windows user's permissions and may access files, processes and networks. Do not execute untrusted code or packages.

## Requirements

- Windows 10/11
- .NET 10 SDK

## Build and run

```powershell
dotnet restore PlayGroundSharp.slnx
dotnet build PlayGroundSharp.slnx -c Debug
dotnet run --project src/PlayGroundSharp.App/PlayGroundSharp.App.csproj -c Debug
```

The App build copies the Worker and its dependencies into the App output's `Worker` directory. User code always runs in that child process, not in WPF.

## Test

```powershell
dotnet test PlayGroundSharp.slnx -c Debug
dotnet build PlayGroundSharp.slnx -c Release
dotnet test PlayGroundSharp.slnx -c Release
```

Tests include stateful Roslyn execution, snapshots, completion, local DLL references, an offline local NuGet feed with a transitive dependency, Named Pipe framing and separate Worker processes.

## Basic operation

- `Ctrl+Enter`: execute the current submission
- `Shift+Enter`: insert a newline
- `Ctrl+Space`: show completion
- `Esc`: close completion; while running, request cancellation
- `Up` / `Down`: move through single-line input history
- Click a previous `In` line: copy it into the current prompt
- **Stop**: request cancellation, then terminate/restart an unresponsive Worker after 1.5 seconds
- **References**: open Packages, Assemblies, Usings and Session views

Examples:

```csharp
var values = Enumerable.Range(1, 10).ToArray();
```

```csharp
values.Where(x => x % 2 == 0)
```

```csharp
await Task.FromResult(42)
```

The transcript follows a REPL layout: each submitted line starts with `>`, its value appears on the following line, and the next active `>` prompt follows immediately. `Last` contains the most recent original result object and `Out[index]` retrieves the original result for a submission.

## Commands

```text
:package add <PackageId> [--version <Version>]
:package list
:reference add "<DLL path>"
:reference list
:using add <Namespace>
:using list
:reset
:clear
```

If package version is omitted, NuGet floating version `*` resolves the latest stable version and the exact selected version is shown. Package restore runs outside the UI thread and uses the normal NuGet cache. Assembly upgrades/removals and conflicting identities require a Worker restart.

## Current limitations

- The default theme is Light; user-selectable theme colors are a low-priority follow-up.
- Result pop-out/detached inspection is a low-priority follow-up.
- Enter-to-execute with incomplete-syntax detection is not implemented; use `Ctrl+Enter`.
- Package removal/upgrades and unloading an existing assembly identity are not supported in-place.
- Result trees are currently rendered as compact text in the transcript rather than an expandable object inspector.
- Quick Info uses mouse hover; signature help uses the same compact popup as completion.
