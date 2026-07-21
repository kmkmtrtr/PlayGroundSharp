using Microsoft.CodeAnalysis.Scripting;
using PlayGroundSharp.Core;
using PlayGroundSharp.TestDependency;
using PlayGroundSharp.TestFixture;
using PlayGroundSharp.Worker;

namespace PlayGroundSharp.Worker.Tests;

[CollectionDefinition("Console", DisableParallelization = true)]
public sealed class ConsoleCollection;

[Collection("Console")]
public sealed class ScriptSessionTests
{
    [Fact]
    public void SharedDefaultReferenceListCoversWorkerFrameworkReferences()
    {
        var workerReferences = ScriptOptions.Default.MetadataReferences
            .Select(static reference => NormalizeUnresolvedReferenceDisplay(reference.Display))
            .Select(Path.GetFileNameWithoutExtension)
            .Append(typeof(object).Assembly.GetName().Name)
            .Append(typeof(Enumerable).Assembly.GetName().Name)
            .Append(typeof(System.Text.Json.JsonElement).Assembly.GetName().Name)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Empty(workerReferences.Except(SessionContext.DefaultReferenceAssemblyNames));
    }

    [Fact]
    public async Task EvaluatesExpression()
    {
        var result = await new ScriptSession().ExecuteAsync(1, "1 + 2");
        Assert.True(result.StateAccepted);
        Assert.Equal("3", result.Snapshot?.Display);
    }

    private static string NormalizeUnresolvedReferenceDisplay(string? display)
    {
        if (display is null) return string.Empty;
        var separator = display.LastIndexOf(": ", StringComparison.Ordinal);
        return separator >= 0 ? display[(separator + 2)..] : display;
    }

    [Fact]
    public async Task EvaluatesTrailingExpressionWithSemicolon()
    {
        var session = new ScriptSession();
        var result = await session.ExecuteAsync(1, "\"fuga\".Any(x => x == 'f');");

        Assert.True(result.HasReturnValue);
        Assert.Equal("true", result.Snapshot?.Display);
    }

    [Fact]
    public async Task CompletesOmittedTrailingSemicolons()
    {
        var session = new ScriptSession();

        Assert.True((await session.ExecuteAsync(1, "var text = \"fuga\"")).StateAccepted);
        Assert.True((await session.ExecuteAsync(2, "bool HasF(string value) => value.Contains('f')")).StateAccepted);
        var recordResult = await session.ExecuteAsync(3, "record Entry(string Value) // semicolon omitted");
        Assert.True(recordResult.StateAccepted,
            string.Join(" | ", recordResult.Diagnostics.Select(static diagnostic => $"{diagnostic.Id}: {diagnostic.Message}")));
        Assert.True((await session.ExecuteAsync(4, "using System.Text")).StateAccepted);

        var result = await session.ExecuteAsync(5, "HasF(new StringBuilder(new Entry(text).Value).ToString())");
        Assert.True(result.HasReturnValue);
        Assert.Equal("true", result.Snapshot?.Display);
    }

    [Fact]
    public async Task ContinuesVariablesMethodsTypesAndAwait()
    {
        var session = new ScriptSession();
        await session.ExecuteAsync(1, "var values = Enumerable.Range(1, 10).ToArray();");
        await session.ExecuteAsync(2, "record User(string Name, int Age);");
        await session.ExecuteAsync(3, "bool IsAdult(User user) => user.Age >= 18;");

        Assert.Equal("55", (await session.ExecuteAsync(4, "values.Sum()" )).Snapshot?.Display);
        Assert.Equal("true", (await session.ExecuteAsync(5, "IsAdult(new User(\"A\", 20))")).Snapshot?.Display);
        Assert.Equal("42", (await session.ExecuteAsync(6, "await Task.FromResult(42)")).Snapshot?.Display);
    }

    [Fact]
    public async Task ExposesExecutionCancellationTokenToSubmittedCode()
    {
        var session = new ScriptSession();
        await session.ExecuteAsync(1, "var retained = 21;");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.ExecuteAsync(2, "await Task.Delay(10_000, ExecutionCancellation)", cancellationToken: cancellation.Token));

        var next = await session.ExecuteAsync(2, "retained * 2");
        Assert.Equal("42", next.Snapshot?.Display);
    }

    [Fact]
    public async Task ReportsRetainedVariablesWithBoundedSnapshots()
    {
        var session = new ScriptSession();
        await session.ExecuteAsync(1, "var number = 42; const string label = \"answer\"; var longText = new string('x', 600); var values = Enumerable.Range(1, 10).ToArray();");
        await session.ExecuteAsync(2, "number = 100;");

        var variables = session.GetVariables();

        var number = Assert.Single(variables, static variable => variable.Name == "number");
        Assert.Equal("System.Int32", number.TypeName);
        Assert.Equal("100", number.Value.Display);
        Assert.False(number.IsReadOnly);
        var label = Assert.Single(variables, static variable => variable.Name == "label");
        Assert.Equal("answer", label.Value.Display);
        Assert.True(label.IsReadOnly);
        var longText = Assert.Single(variables, static variable => variable.Name == "longText");
        Assert.Equal(512, longText.Value.Display?.Length);
        Assert.True(longText.Value.IsTruncated);
        var values = Assert.Single(variables, static variable => variable.Name == "values");
        Assert.Equal(10, values.Value.Items?.Count);
        Assert.Equal("10", values.Value.Items?[9].Display);
    }

    [Fact]
    public async Task CompilationAndRuntimeErrorsDoNotPreventFollowingSubmissions()
    {
        var session = new ScriptSession();
        await session.ExecuteAsync(1, "var value = 10;");
        var compileError = await session.ExecuteAsync(2, "missing +");
        var runtimeError = await session.ExecuteAsync(3, "throw new InvalidOperationException(\"boom\");");
        var next = await session.ExecuteAsync(4, "value * 2");

        Assert.False(compileError.StateAccepted);
        Assert.NotEmpty(compileError.Diagnostics);
        Assert.NotNull(runtimeError.Exception);
        Assert.Equal("20", next.Snapshot?.Display);
    }

    [Fact]
    public async Task BoundsRuntimeExceptionMessages()
    {
        var result = await new ScriptSession().ExecuteAsync(
            1,
            "throw new InvalidOperationException(new string('x', 100_000));");

        Assert.NotNull(result.Exception);
        Assert.Equal(ResultSnapshotFactory.MaximumExceptionTextLength, result.Exception.Message.Length);
    }

    [Fact]
    public async Task BrokenResultEnumerationDoesNotDesynchronizeTheSession()
    {
        var session = new ScriptSession();
        await session.ExecuteAsync(1,
            "IEnumerable<int> Broken() { yield return 1; throw new InvalidOperationException(\"broken sequence\"); }");

        var broken = await session.ExecuteAsync(2, "Broken()");
        var next = await session.ExecuteAsync(3, "40 + 2");

        Assert.True(broken.StateAccepted);
        Assert.Equal(SnapshotKind.Sequence, broken.Snapshot?.Kind);
        Assert.Equal("1", broken.Snapshot?.Items?[0].Display);
        Assert.Equal(SnapshotKind.Exception, broken.Snapshot?.Items?[1].Kind);
        Assert.Contains("broken sequence", broken.Snapshot?.Items?[1].Display, StringComparison.Ordinal);
        Assert.Equal("42", next.Snapshot?.Display);
        Assert.Equal(3, session.Context.Submissions.Count);
    }

    [Fact]
    public async Task CapturesConsoleAndKeepsOriginalResults()
    {
        var session = new ScriptSession();
        var output = new List<string>();
        var error = new List<string>();
        await session.ExecuteAsync(1, "Console.WriteLine(\"out\"); Console.Error.WriteLine(\"err\"); 21 * 2", output.Add, error.Add);

        Assert.Contains("out", string.Concat(output));
        Assert.Contains("err", string.Concat(error));
        Assert.Equal("42", (await session.ExecuteAsync(2, "Last")).Snapshot?.Display);
        Assert.Equal("42", (await session.ExecuteAsync(3, "Out[1]")).Snapshot?.Display);
    }

    [Fact]
    public async Task BatchesRapidConsoleLinesWithoutLosingContent()
    {
        var output = new List<string>();

        await new ScriptSession().ExecuteAsync(
            1,
            "foreach (var i in Enumerable.Range(0, 10_000)) Console.WriteLine(i);",
            output.Add);

        var combined = string.Concat(output);
        Assert.True(output.Count < 10, $"Expected batched output but received {output.Count} events.");
        Assert.StartsWith("0", combined, StringComparison.Ordinal);
        Assert.Contains("9999", combined, StringComparison.Ordinal);
        Assert.Equal(10_000, combined.Count(static character => character == '\n'));
    }

    [Fact]
    public async Task StreamsConsoleProgressBeforeSubmissionCompletes()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var firstOutput = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = new ScriptSession().ExecuteAsync(
            1,
            "Thread.Sleep(200); Console.WriteLine(\"first\"); Thread.Sleep(500);",
            text =>
            {
                if (text.Contains("first", StringComparison.Ordinal))
                    firstOutput.TrySetResult(stopwatch.Elapsed);
            });

        var firstOutputAt = await firstOutput.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await execution;
        var completionAt = stopwatch.Elapsed;

        Assert.True(completionAt - firstOutputAt >= TimeSpan.FromMilliseconds(350),
            $"Console progress arrived only {completionAt - firstOutputAt:g} before completion.");
    }

    [Fact]
    public async Task BoundsAndBatchesVeryLargeConsoleOutputInOrder()
    {
        const int maximumCharacters = 10 * 1024 * 1024;
        var output = new List<string>();

        await new ScriptSession().ExecuteAsync(
            1,
            $"Console.Write(new string('x', {maximumCharacters + 100}));",
            output.Add);

        var combined = string.Concat(output);
        Assert.True(output.Count < 200, $"Expected batched output but received {output.Count} events.");
        Assert.Equal(maximumCharacters, combined.IndexOf('\n'));
        Assert.Contains("output truncated at 10 MiB", combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapturesLargeArrayBeyondTheFormerPreviewLimit()
    {
        var result = await new ScriptSession().ExecuteAsync(1, "Enumerable.Range(1, 1000).ToArray()");

        Assert.True(result.StateAccepted);
        Assert.Equal(1000, result.Snapshot?.TotalCount);
        Assert.Equal(1000, result.Snapshot?.Items?.Count);
        Assert.False(result.Snapshot?.IsTruncated);
    }

    [Fact]
    public async Task AddsLocalDllReferences()
    {
        var session = new ScriptSession();
        session.AddReference(typeof(DependencyValue).Assembly.Location);
        session.AddReference(typeof(Greeter).Assembly.Location);
        var result = await session.ExecuteAsync(1, "PlayGroundSharp.TestFixture.Greeter.Message");
        Assert.Equal("hello from fixture", result.Snapshot?.Display);
    }

    [Fact]
    public async Task AddedUsingEnablesAnExtensionMethodAfterSessionStateExists()
    {
        var session = new ScriptSession();
        session.AddReference(typeof(NumberExtensions).Assembly.Location);
        var declaration = await session.ExecuteAsync(1, "var value = 2;");

        session.AddUsing("PlayGroundSharp.TestFixture");
        var result = await session.ExecuteAsync(2, "value.Billions()");

        Assert.True(declaration.StateAccepted);
        Assert.True(result.StateAccepted, string.Join(Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.Equal("2000000000", result.Snapshot?.Display);
    }

    [Fact]
    public async Task RemovesAndRestoresUsingForFutureSubmissions()
    {
        var session = new ScriptSession();
        session.RemoveUsing("System.Linq");
        var withoutUsing = await session.ExecuteAsync(1, "Enumerable.Range(1, 3).Sum()");

        Assert.False(withoutUsing.StateAccepted);
        Assert.DoesNotContain("System.Linq", session.Context.Imports);

        session.AddUsing("System.Linq");
        var restored = await session.ExecuteAsync(2, "Enumerable.Range(1, 3).Sum()");

        Assert.True(restored.StateAccepted);
        Assert.Equal("6", restored.Snapshot?.Display);
    }

    [Fact]
    public async Task RejectsUsingRemovalAfterSessionStateExists()
    {
        var session = new ScriptSession();
        await session.ExecuteAsync(1, "var marker = 1;");

        var error = Assert.Throws<InvalidOperationException>(() => session.RemoveUsing("System.Linq"));

        Assert.Contains("fresh Worker", error.Message, StringComparison.Ordinal);
        Assert.Contains("System.Linq", session.Context.Imports);
    }

    [Fact]
    public async Task UsesBoundedLargeDataHelpersFromSessionGlobals()
    {
        var path = Path.Combine(Path.GetTempPath(), $"PlayGroundSharp-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "abcdefghij");
        try
        {
            var escapedPath = path.Replace("\"", "\"\"");
            var result = await new ScriptSession().ExecuteAsync(1, $"Data.PreviewText(@\"{escapedPath}\", 5)");

            Assert.True(result.StateAccepted);
            Assert.Equal("abcde", result.Snapshot?.Display);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecutesGeneratedMultiFileSnippets()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PlayGroundSharp-Batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var paths = new[] { Path.Combine(directory, "one.json"), Path.Combine(directory, "two.json") };
        await File.WriteAllTextAsync(paths[0], "{\"id\":1}");
        await File.WriteAllTextAsync(paths[1], "{\"id\":2}");
        try
        {
            var session = new ScriptSession();

            var inspections = await session.ExecuteAsync(1, DataSnippetBuilder.CreateFileInspection(paths));
            var json = await session.ExecuteAsync(2, DataSnippetBuilder.CreateJsonBatch(paths));

            Assert.True(inspections.StateAccepted,
                string.Join(" | ", inspections.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            Assert.Equal(2, inspections.Snapshot?.TotalCount);
            Assert.True(json.StateAccepted,
                string.Join(" | ", json.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            Assert.Equal(2, json.Snapshot?.TotalCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
