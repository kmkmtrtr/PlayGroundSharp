# PlayGroundSharp

PlayGroundSharp is a Windows C# interactive console inspired by Chrome DevTools Console. Submit one expression at a time while variables, methods, types, usings and references remain available in later submissions.

> **Security:** PlayGroundSharp is not a sandbox. Submitted C# and packages run with the current Windows user's permissions and may access files, processes and networks. Do not execute untrusted code or packages.

## Requirements

- Windows 10/11
- .NET 10 SDK for building; the published executable requires the .NET 10 Desktop Runtime (x64)

## Build and run

```powershell
dotnet restore PlayGroundSharp.slnx
dotnet build PlayGroundSharp.slnx -c Debug
dotnet run --project src/PlayGroundSharp.App/PlayGroundSharp.App.csproj -c Debug
```

The App starts the same executable in hidden `--worker` mode as a separate child process. User code always runs in that child process, not in WPF.

## Publish

```powershell
dotnet publish src/PlayGroundSharp.App/PlayGroundSharp.App.csproj -c Release
```

Release publishing is configured in the App project for `win-x64`, framework-dependent, single-file output. The result is one `PlayGroundSharp.App.exe` under `bin/Release/net10.0-windows/win-x64/publish`. Roslyn requires physical metadata files, so .NET extracts the bundled managed content to its per-user single-file cache at startup; no separate Worker executable is distributed. Install the .NET 10 Desktop Runtime on the destination machine.

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
- **Types**: open a searchable, kind-colored namespace/type/method tree built from active usings, session declarations and dynamically added libraries; each row shows its kind, and type details include direct base classes/interfaces alongside the signature and XML documentation
- Hover a symbol for a compact signature/summary popup; click it to pin full details. Framework symbols can open their localized Microsoft Learn API page.
- **Session**: open Variables, NuGet, Libraries, Usings and settings; the Usings page can add or remove namespaces
- Drag the Explorer and Workspace dividers to resize either sidebar; use a horizontal wheel or `Shift+wheel` to scroll deep Explorer hierarchies
- Drag the divider inside completion to resize the candidate and documentation panes
- **Language**: switch the application UI between Japanese and English; Japanese is the default for new settings
- **NuGet**: search nuget.org, review package metadata and install the displayed exact version
- **Libraries**: list imported packages, local DLLs and package runtime assemblies with versions and sources
- **Inspect** on a result: open its bounded property/item tree in a separate window
- **File**: save or open a `.pgsworkspace` containing submissions, draft input, usings, DLL references and exact package versions
- **Data**: insert bounded or streaming snippets for large text, byte, JSON-array and JSON Lines files
- **Help** or `F1`: open the built-in guide for input, IntelliSense, symbols, workspaces, large files, dependencies and security

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

## Workspaces

Workspace files do not serialize live objects or Roslyn state. Opening one starts a fresh Worker, restores packages/references/usings, and replays accepted submissions in order. This preserves a portable, inspectable format, but **side effects such as file writes or process launches run again**. Missing local DLLs are reported and skipped. Workspace files are limited to 16 MiB and 10,000 entries per section.

## Large files and JSON

`Data` is available in every Worker session:

```csharp
Data.Inspect(@"C:\data\large.json")
Data.PreviewText(@"C:\data\large.log", 65536)
Data.ReadLines(@"C:\data\large.csv").Take(100)
await Data.ReadJsonArrayAsync(@"C:\data\large.json", take: 100)
```

`PreviewText` and `ReadBytes` are capped at 1 MiB per call. JSON arrays are streamed and retain only the requested items (maximum 10,000); JSON Lines is exposed as an `IAsyncEnumerable<JsonElement>`. The existing result snapshot limit still displays at most 100 collection items.

## Commands

```text
:help
:package add <PackageId> [--version <Version>]
:package list
:reference add "<DLL path>"
:reference list
:using add <Namespace>
:using remove <Namespace>
:using list
:reset
:clear
```

If package version is omitted, NuGet floating version `*` resolves the latest stable version and the exact selected version is shown. Package restore runs outside the UI thread and uses the normal NuGet cache. Assembly upgrades/removals and conflicting identities require a Worker restart.

Typing `:` opens command completion. After adding a DLL or package, `:using add ` completion lists namespaces found in those assemblies, while `:using remove ` lists active imports. C# type completion can also show an unimported added-library type with its namespace; accepting it with `Tab` adds the required using to both execution and IntelliSense contexts. Removing an import after code has run starts a fresh Worker because Roslyn script continuations inherit previous imports; current variables, methods and types are therefore cleared after confirmation, while transcript, history and dependencies remain. The NuGet browser discovers the official V3 `SearchQueryService` endpoint from the nuget.org service index rather than relying on a fixed search host.

## Current limitations

- Package removal/upgrades and unloading an existing assembly identity are not supported in-place.
- NuGet browsing currently targets nuget.org; additional configured package sources are not shown in the browser.
- Variable values are preview snapshots and are truncated to 512 characters in the list.
- Quick Info uses mouse hover; signature help and completion share the compact assistance popup.
- BenchmarkDotNet integration is deferred; the planned design uses a disposable benchmark Worker instead of running benchmarks inside the stateful interactive Worker.
- Framework and package XML documentation is displayed in the language supplied by that library; switching the application language translates the UI labels, not third-party documentation text. Framework entries link to localized Microsoft Learn instead of applying an unreliable automatic translation.
- Workspace loading replays code rather than serializing live objects, so non-deterministic or side-effecting submissions may produce different state.
