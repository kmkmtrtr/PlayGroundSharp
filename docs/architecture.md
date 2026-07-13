# PlayGroundSharp architecture

## Phase 0 environment

The repository started empty. .NET SDK 10.0.109 and Windows Desktop Runtime 10.0.9 are installed. Roslyn continuity (values, variables, methods, types, await, runtime-error continuation), completion (arrays, LINQ extensions, session types), dynamic DLL references and offline package restore with a transitive dependency are retained as automated tests rather than disposable spikes.

## Components

- **App**: WPF/MVVM shell, transcript, AvalonEdit input, language-service presentation and Worker lifecycle.
- **Core**: versioned IPC envelopes, request/event DTOs, diagnostics and bounded result contracts.
- **Worker**: owns `ScriptState<object?>`, original result objects, references, console capture and package restore.
- **LanguageService**: builds an in-memory Roslyn script document from accepted history, imports and references.

## IPC

The App starts one child Worker with a random pipe name. A dedicated duplex named pipe carries newline-delimited JSON envelopes, so submitted `Console` output cannot corrupt IPC. Every envelope includes a protocol version, kind and correlation ID. Only neutral DTOs cross the boundary. After each submission, the Worker also publishes bounded variable snapshots; the App never evaluates session variables or loads submitted assemblies into WPF.

## Script state

The first submission uses `CSharpScript.RunAsync`; accepted submissions use `ContinueWithAsync`. Compilation failures do not replace state. Runtime-faulted states remain continuable. `SessionGlobals` exposes original values through `Last` and indexed `Out`.

## Language workspace

An `AdhocWorkspace` hosts a script-kind document composed from accepted submissions and current input. It uses the same imports and metadata paths as execution. Completion, Quick Info and diagnostics come from that document. The left Type Explorer uses the same compilation to enumerate public types directly available from active namespaces, then merges session-defined declarations and public metadata types from dynamic DLL/package references. It refreshes asynchronously after accepted submissions and reference/import changes, so WPF never reflects over user objects or blocks its UI thread.

## Result snapshots

The Worker converts values into bounded `ResultSnapshot` trees. It detects cycles by reference identity, limits depth to 10, collections to 100 and strings to 1 MiB. JSON is formatted in the Worker. Live arbitrary objects never reach the UI. Transcript results retain these neutral trees so the detached Result Inspector can expand properties and sequence items without contacting the Worker again.

## NuGet strategy

Package restore uses a temporary SDK project and `dotnet restore`, then reads `obj/project.assets.json`. This delegates target-framework and transitive asset selection to the .NET SDK, supports local-feed tests, and reuses the global package cache. Temporary files are deleted in `finally`.

Package browsing also stays outside the WPF process. The App sends a typed search request to the Worker, which discovers `SearchQueryService` from the NuGet V3 service index, performs an asynchronous bounded query, and returns neutral package metadata DTOs. Automated search tests use a fake HTTP handler and never contact public NuGet.

## Assembly loading and reconstruction

Reference paths are added to Roslyn ScriptOptions and the language workspace. The MVP does not replace an already loaded assembly with the same identity. Package upgrades, removals and identity conflicts explicitly require Worker reconstruction.

## Security

PlayGroundSharp is not a sandbox. Code runs with the current Windows user's authority and can access files, processes and networks. Process separation protects UI availability and permits crash/loop recovery. Untrusted code or packages must not be executed.

## User settings

Execution-key and Light/Dark theme preferences are stored under the user's local application data. Theme changes mutate shared WPF brush resources; language analysis and execution behavior are unaffected.

## Benchmarking extension point

BenchmarkDotNet should not execute inside the long-lived interactive Worker: generated benchmark hosts, tiered compilation, process launches and assembly loading would pollute session state and make cancellation/recovery harder. A future `:benchmark` workflow should snapshot the selected expression or method into a temporary generated benchmark project, run BenchmarkDotNet in a disposable child Worker, stream progress, and return only a neutral summary/table to the transcript. This remains a low-priority follow-up.

## Validation summary

Debug and Release builds complete with zero warnings. The automated suite covers Core framing, Worker state/snapshots/package restore, LanguageService completion/diagnostics and separate-process integration including infinite-loop termination. UI Automation smoke tests exercise the actual WPF prompt, transcript, package restore and Worker recovery; details are in `docs/manual-testing.md`.
