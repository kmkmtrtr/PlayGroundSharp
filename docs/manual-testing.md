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
| 11 | `JsonNode.Parse("{\"answer\":42}")` | formatted JSON result |
| 12 | `Enumerable.Range(1, 101)` | 100 items and truncation marker |
| 13 | Execute `var answer = 42`, open **Session > Variables** | `answer`, `System.Int32`, and `42` are shown |
| 14 | Execute an object/sequence and click **Inspect** | a separate expandable snapshot tree opens |
| 15 | Switch **Session > Theme** to Dark and restart | dark colors apply immediately and persist |
| 16 | Add the fixture DLL, type `new Gree`, press `Ctrl+Space`, accept `Greeter` with `Tab` | the type is inserted and its namespace is added to Usings |
| 17 | Open **Session > NuGet**, search `Humanizer.Core`, then click **Install** | results show package metadata; the exact displayed version is restored |
| 18 | After package/DLL additions, open **Session > Libraries** | package and assembly names, versions and sources are listed |

Public-package manual verification uses `Humanizer.Core` version `3.0.10`, selected as a small stable .NET 8+/netstandard-compatible package. Automated package tests do not use public NuGet; they create and restore fixture packages through a local feed.

## Verification record (2026-07-13)

The Debug WPF application was launched and driven through its actual AvalonEdit prompt using Windows UI Automation.

- Verified in the real UI: AC-01, AC-02, AC-03, AC-05, AC-06, AC-07, AC-08, AC-10, AC-11 and AC-12.
- AC-04: the real UI opened an inline completion panel with 134 candidates; automated language-service assertions confirm `Length`, `Where`, `Select`, `Sum` and `ToArray`.
- AC-09: automated Worker and LanguageService tests confirm the built local fixture DLL works in execution and completion. The manual `:reference add` path flow was not separately UI-automated.
- Startup/lifecycle: the window title was `PlayGroundSharp`, one Worker was connected, App exit code was 0, and no Worker remained after normal shutdown.
- Public NuGet: `Humanizer.Core 3.0.10` restored through the UI; after `:using add Humanizer`, `"hello_world".Humanize()` returned `hello world`.
