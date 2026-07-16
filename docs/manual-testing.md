# Manual acceptance procedure

Build and start the Debug App, then execute each item in a fresh session where noted.

| AC | Procedure | Expected |
|---|---|---|
| 01 | `1 + 2` | the line below the submitted `>` prompt shows `3` |
| 02 | `var values = Enumerable.Range(1, 10).ToArray();`, then `values.Sum()` | `55` |
| 03 | Submit invalid code, then `values.Sum()` | diagnostics, then still `55` |
| 04 | Type `values.` | `Length`, `Where`, `Select` completion |
| 05 | Type `string.Join(` | overload signatures |
| 06 | `await Task.FromResult(42)` | `42` |
| 07 | `Console.WriteLine("message"); 42` | separate stdout and `Out` lines |
| 08 | `:package add Humanizer.Core --version 3.0.10`, `:using add Humanizer`, then `"hello_world".Humanize()` | exact version shown and `hello world` |
| 09 | Add the built fixture DLL and type its namespace | execution and completion see fixture type |
| 10 | Submit `while (true) { }`, then Stop | App survives; Worker restarts; state-loss notice |
| 11 | `JsonNode.Parse("{\"hoge\":[1,2,3,4,5,6],\"fuga\":[{\"piyo\":123},{\"piyo\":456}]}")` | a Chrome-style expandable root summary appears; `hoge` and `fuga` can be expanded independently |
| 12 | `Enumerable.Range(0, 250).ToArray()` | the inline tree exposes all captured values through `[0 … 99]`, `[100 … 199]` and `[200 … 249]` range nodes |
| 13 | Execute `var answer = 42`, open **Session > Variables** | `answer`, `System.Int32`, and `42` are shown |
| 14 | Execute an object/sequence and click **Inspect** | a separate expandable snapshot tree opens |
| 15 | Switch **Session > Theme** to Dark and restart | dark colors apply immediately and persist |
| 16 | Add the fixture DLL, type `new Gree`, press `Ctrl+Space`, accept `Greeter` with `Tab` | the type is inserted and its namespace is added to Usings |
| 17 | Open **Session > NuGet**, search `Humanizer.Core`, then click **Install** | results show package metadata; the exact displayed version is restored |
| 18 | After package/DLL additions, open **Session > Libraries** | package and assembly names, versions and sources are listed |
| 19 | Open **Types**, search `Contains(string value)`, then select the method | `System > String > Contains(string value)` and its Summary/Parameters documentation are shown |
| 20 | Open **Session > Settings**, switch **Language** between Japanese and English | headers, controls, status and Explorer documentation labels update immediately and persist |
| 21 | Open **Session > Usings**, add a namespace, then remove it | the list and completion update; after prior execution, removal warns that session variables/types will be cleared and restarts the Worker |
| 22 | Open **Types** and expand namespaces containing classes, interfaces, structs, enums and methods; select a derived class | rows show compact kind labels with distinct theme-appropriate colors, and the tooltip/detail panel lists its direct base class and interfaces |
| 23 | Run `dotnet publish src/PlayGroundSharp.App/PlayGroundSharp.App.csproj -c Release`, then start the published executable | the publish directory contains only `PlayGroundSharp.App.exe`; the GUI connects to a separate process of the same executable in Worker mode |
| 24 | Start the Web preview, type `Enumerable.Ran`, then press `Tab` | only matching completion candidates remain; `Enumerable.Range` is inserted and Summary is shown |
| 25 | In the Web preview, search and add `Humanizer.Core`, then type `"hello_world".Hum` | Humanizer extension candidates and their namespace are available to completion |
| 26 | In the Web preview, select the built fixture DLL and type `new PlayGroundSharp.TestFixture.Gre` | the DLL appears in Files and `Greeter` is offered by completion |
| 27 | In the Web preview, execute stateful code and save a workspace | a `.pgsworkspace` download starts; loading it in Files restores imports, packages, input and accepted submissions |

Public-package manual verification uses `Humanizer.Core` version `3.0.10`, selected as a small stable .NET 8+/netstandard-compatible package. Automated package tests do not use public NuGet; they create and restore fixture packages through a local feed.

## Verification record (2026-07-13)

The Debug WPF application was launched and driven through its actual AvalonEdit prompt using Windows UI Automation.

- Verified in the real UI: AC-01, AC-02, AC-03, AC-05, AC-06, AC-07, AC-08, AC-10, AC-11 and AC-12.
- AC-04: the real UI opened an inline completion panel with 134 candidates; automated language-service assertions confirm `Length`, `Where`, `Select`, `Sum` and `ToArray`.
- AC-09: automated Worker and LanguageService tests confirm the built local fixture DLL works in execution and completion. The manual `:reference add` path flow was not separately UI-automated.
- Startup/lifecycle: the window title was `PlayGroundSharp`, one Worker was connected, App exit code was 0, and no Worker remained after normal shutdown.
- Public NuGet: `Humanizer.Core 3.0.10` restored through the UI; after `:using add Humanizer`, `"hello_world".Humanize()` returned `hello world`.
- AC-19/20 (2026-07-14): UI Automation confirmed Japanese labels and the expanded `System > String > Contains(string value)` method result; LanguageService tests verify framework, session and dynamic-DLL Summary/Param extraction.
