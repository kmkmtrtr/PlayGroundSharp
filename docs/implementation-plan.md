# PlayGroundSharp implementation plan / WBS

## Goal

Deliver a vertical .NET 10 WPF MVP: the UI sends incremental C# submissions over a named pipe to a child Worker, the Worker preserves Roslyn state, and bounded snapshots/results stream into a dense transcript. IntelliSense and execution use the same history and references.

## WBS

| ID | Work item | Deliverable | Verification |
|---|---|---|---|
| 0.1 | Inspect repository and SDK | Environment/risk record | `dotnet --info` |
| 0.2 | Prove Roslyn continuity | Value, variable, method, type, await tests | Worker tests |
| 0.3 | Prove language services | History-aware completion/diagnostics | Language tests |
| 0.4 | Prove dynamic metadata | DLL and package loading tests | Local fixtures |
| 1.1 | Establish solution | CPM, nullable, projects/references | Restore/build |
| 1.2 | Define Core contracts | Requests, events, diagnostics, snapshots, framing | Core tests |
| 1.3 | Implement Worker session | Stateful execution, Last/Out, console, cancellation | Worker tests |
| 1.4 | Implement process IPC | Named-pipe host/client and lifecycle | Integration tests |
| 2.1 | Build MVVM shell | Transcript, editor, status bar | Build/manual check |
| 2.2 | Wire execution flow | configurable Enter/Ctrl+Enter, output, errors, reset/stop | AC-01/02/03/06/07 |
| 3.1 | Build Roslyn workspace | Shared history/references and completion | Language tests |
| 3.2 | Add editor assistance | Completion, quick info, signature surface | AC-04/05 |
| 4.1 | Parse commands | package/reference/using/reset/clear | Unit tests |
| 4.2 | Resolve references | Local DLL, NuGet assets and cache | Local-feed tests |
| 4.3 | Refresh both engines | Execution/completion synchronization | AC-08/09 |
| 5.1 | Harden value handling | Depth/cycle/item/string/output limits | Snapshot tests |
| 5.2 | Harden lifecycle | Cancel, kill/restart, state-loss notice | AC-10 |
| 5.3 | Document and validate | README, architecture, Debug/Release | Full validation |
| 6.1 | Add theme selection | User-selectable Light/Dark colors and persistence | AC-15 |
| 6.2 | Add result pop-out | Detachable expandable snapshot viewer | AC-14 |
| 6.3 | Add NuGet browser | Worker-side V3 search and exact-version install | AC-17 |
| 6.4 | Consolidate session inventory | Variables, imported libraries and usings | AC-13/18 |
| 7.1 | Prototype benchmark runner | Disposable BenchmarkDotNet Worker and neutral summary | Deferred |

## Risks and mitigations

- Script and workspace semantics can diverge: one session context owns history, imports and reference paths; both paths are tested.
- Infinite loops can ignore cancellation: request cooperative cancellation, then kill/restart the Worker after a grace period.
- Arbitrary objects are unsafe over IPC: reflect/enumerate only in the Worker into bounded neutral DTOs.
- Loaded assembly identities cannot be replaced safely: report that a Worker reconstruction is required.
- NuGet asset selection is subtle: use an isolated SDK restore project and consume `project.assets.json`.
- WPF automation may be unavailable: ViewModel, IPC, Worker, commands and language services stay headless-testable.

## Gates

Each phase runs `dotnet restore`, `dotnet build -c Debug`, and `dotnet test -c Debug`. Final validation also runs Release build/tests. Failures are investigated before proceeding.
