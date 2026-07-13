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

- `Enter`: execute the current submission (default)
- A trailing `;` may be omitted from declarations, expression-bodied methods, records and `using` directives
- `Shift+Enter`: insert a newline
- The execution key can be switched to `Ctrl+Enter` in **Session** settings
- `Ctrl+Space`: show completion
- `Tab`: accept the selected completion; added-library types can add their required `using` automatically
- `Esc`: close completion; while running, request cancellation
- `Up` / `Down`: move through single-line input history
- Click a previous `In` line: copy it into the current prompt
- **Stop**: request cancellation, then terminate/restart an unresponsive Worker after 1.5 seconds
- **Session**: open Variables, Packages, Assemblies, Usings and settings
- **Inspect** on a result: open its bounded property/item tree in a separate window

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
:help
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

Typing `:` opens command completion. After adding a DLL or package, `:using add ` completion lists namespaces found in those assemblies. C# type completion can also show an unimported added-library type with its namespace; accepting it with `Tab` adds the required using to both execution and IntelliSense contexts.

## Current limitations

- Package removal/upgrades and unloading an existing assembly identity are not supported in-place.
- NuGet package-ID completion does not perform online search; enter the package ID explicitly.
- Variable values are preview snapshots and are truncated to 512 characters in the list.
- Quick Info uses mouse hover; signature help and completion share the compact assistance popup.
