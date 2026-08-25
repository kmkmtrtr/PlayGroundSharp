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
    public async Task CanExecuteWithAnOlderInstalledFrameworkReferencePack()
    {
        var framework = DotNetFrameworkLocator.Discover()
            .FirstOrDefault(static candidate => candidate.Version.Major == 9);
        if (framework is null) return;

        var references = framework.GetReferencePaths();
        var session = new ScriptSession(references, framework.TargetFramework);

        var result = await session.ExecuteAsync(1, "1 + 2");

        Assert.True(result.StateAccepted);
        Assert.Equal(3, result.ReturnValue);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.Equal(3, (await session.ExecuteAsync(2, "Out[1]")).ReturnValue);
        Assert.Equal(
            nameof(LargeDataAccess),
            (await session.ExecuteAsync(3, "Data.GetType().Name")).ReturnValue);

        var recoverySession = new ScriptSession(references, framework.TargetFramework);
        var unavailableApi = await recoverySession.ExecuteAsync(
            1,
            "new[] { 1 }.LeftJoin(new[] { 1 }, x => x, x => x, (left, right) => left)");

        Assert.False(unavailableApi.StateAccepted);
        Assert.Contains(unavailableApi.Diagnostics, static diagnostic =>
            diagnostic.Level == DiagnosticLevel.Error &&
            diagnostic.Message.Contains("LeftJoin", StringComparison.Ordinal));
        Assert.Equal(3, (await recoverySession.ExecuteAsync(1, "1 + 2")).ReturnValue);
    }

    [Fact]
    public void SharedDefaultReferenceListCoversWorkerFrameworkReferences()
    {
        var workerReferences = ScriptOptions.Default.MetadataReferences
            .Select(static reference => NormalizeUnresolvedReferenceDisplay(reference.Display))
            .Select(Path.GetFileNameWithoutExtension)
            .Append(typeof(object).Assembly.GetName().Name)
            .Append(typeof(Enumerable).Assembly.GetName().Name)
            .Append(typeof(System.Text.Json.JsonElement).Assembly.GetName().Name)
            .Append(typeof(Microsoft.CSharp.RuntimeBinder.Binder).Assembly.GetName().Name)
            .Append(typeof(System.Numerics.BigInteger).Assembly.GetName().Name)
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

    [Fact]
    public async Task PreservesNamedAndInferredTupleElementNamesInResultSnapshots()
    {
        var session = new ScriptSession();

        var named = await session.ExecuteAsync(1, "(Name: \"Ada\", Age: 30)");
        var inferred = await session.ExecuteAsync(
            2,
            "var name = \"Grace\"; var age = 37; var person = (name, age); person");

        Assert.Equal(
            ["Name", "Age"],
            named.Snapshot?.Properties?.Select(static property => property.Name));
        Assert.Equal(
            ["Ada", "30"],
            named.Snapshot?.Properties?.Select(static property => property.Value.Display));
        Assert.Equal(
            ["name", "age"],
            inferred.Snapshot?.Properties?.Select(static property => property.Name));
        Assert.Equal(
            ["Grace", "37"],
            inferred.Snapshot?.Properties?.Select(static property => property.Value.Display));
    }

    [Fact]
    public async Task PreservesNestedAndLongTupleElementNames()
    {
        var result = await new ScriptSession().ExecuteAsync(
            1,
            "(Person: (Name: \"Ada\", Age: 30), Second: 2, Third: 3, Fourth: 4, " +
            "Fifth: 5, Sixth: 6, Seventh: 7, Eighth: 8)");

        var properties = Assert.IsAssignableFrom<IReadOnlyList<ResultProperty>>(result.Snapshot?.Properties);
        Assert.Equal(
            ["Person", "Second", "Third", "Fourth", "Fifth", "Sixth", "Seventh", "Eighth"],
            properties.Select(static property => property.Name));
        Assert.Equal("8", properties[^1].Value.Display);
        var person = Assert.IsAssignableFrom<IReadOnlyList<ResultProperty>>(properties[0].Value.Properties);
        Assert.Equal(["Name", "Age"], person.Select(static property => property.Name));
        Assert.Equal(["Ada", "30"], person.Select(static property => property.Value.Display));
    }

    [Fact]
    public async Task PreservesTupleElementNamesInTaskWhenAllArrays()
    {
        var result = await new ScriptSession().ExecuteAsync(
            1,
            """
            var tasks = Enumerable.Range(1, 3).Select(value =>
                Task.FromResult((index: value, delay: value * 10)));
            await Task.WhenAll(tasks)
            """);

        var items = Assert.IsAssignableFrom<IReadOnlyList<ResultSnapshot>>(result.Snapshot?.Items);
        Assert.Equal(3, items.Count);
        Assert.All(items, static item => Assert.Equal(
            ["index", "delay"],
            item.Properties?.Select(static property => property.Name)));
        Assert.Equal(
            ["1", "10"],
            items[0].Properties?.Select(static property => property.Value.Display));
    }

    [Fact]
    public async Task PreservesTupleElementNamesInGenericSequences()
    {
        var result = await new ScriptSession().ExecuteAsync(
            1,
            "Enumerable.Range(1, 2).Select(value => (index: value, square: value * value)).ToList()");

        var items = Assert.IsAssignableFrom<IReadOnlyList<ResultSnapshot>>(result.Snapshot?.Items);
        Assert.All(items, static item => Assert.Equal(
            ["index", "square"],
            item.Properties?.Select(static property => property.Name)));
    }

    [Fact]
    public async Task FallsBackToRuntimeTupleNamesWhenCompileTimeNamesAreUnavailable()
    {
        var result = await new ScriptSession().ExecuteAsync(1, "(object)(Name: \"Ada\", Age: 30)");

        Assert.Equal(
            ["Item1", "Item2"],
            result.Snapshot?.Properties?.Select(static property => property.Name));
    }

    [Fact]
    public async Task InspectionPreservesTupleElementNames()
    {
        var inspection = await new ScriptSession().InspectExpressionAsync("(Left: 1, Right: 2)");

        Assert.Equal(
            ["Left", "Right"],
            inspection.Snapshot?.Properties?.Select(static property => property.Name));
    }

    [Fact]
    public async Task InspectionEvaluatesWithoutChangingSubmissionStateOrResultHistory()
    {
        var session = new ScriptSession();
        await session.ExecuteAsync(1, "var rows = new[] { new { Price = 10, Quantity = 2 } }; 7");

        var inspection = await session.InspectExpressionAsync(
            "rows.Select(row => new { row.Price, Total = row.Price * row.Quantity })");

        Assert.Empty(inspection.Diagnostics);
        Assert.Null(inspection.Exception);
        var item = Assert.Single(inspection.Snapshot?.Items ?? []);
        Assert.Equal("20", item.Properties?.Single(property => property.Name == "Total").Value.Display);
        Assert.DoesNotContain(session.GetVariables(), static variable => variable.Name == "Total");
        Assert.Equal("7", (await session.ExecuteAsync(2, "Last")).Snapshot?.Display);
    }

    [Fact]
    public async Task InspectionReturnsCompilationAndRuntimeFailuresWithoutAcceptingThem()
    {
        var session = new ScriptSession();
        await session.ExecuteAsync(1, "var value = 21;");

        var compilation = await session.InspectExpressionAsync("missing + 1");
        var runtime = await session.InspectExpressionAsync("throw new InvalidOperationException(\"preview\")");
        var next = await session.ExecuteAsync(2, "value * 2");

        Assert.Contains(compilation.Diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.Contains("preview", runtime.Exception?.Message, StringComparison.Ordinal);
        Assert.Equal("42", next.Snapshot?.Display);
    }

    [Fact]
    public async Task SupportsDynamicMemberBinding()
    {
        var result = await new ScriptSession().ExecuteAsync(
            1,
            "dynamic value = new { Number = 21 }; (int)value.Number * 2");

        Assert.True(result.StateAccepted,
            string.Join(" | ", result.Diagnostics.Select(static diagnostic => $"{diagnostic.Id}: {diagnostic.Message}")));
        Assert.Equal("42", result.Snapshot?.Display);
    }

    [Fact]
    public async Task SupportsExtendedFrameworkNumericTypes()
    {
        var result = await new ScriptSession().ExecuteAsync(
            1,
            "System.Numerics.BigInteger.Parse(\"123456789012345678901234567890\") + 1");

        Assert.True(result.StateAccepted,
            string.Join(" | ", result.Diagnostics.Select(static diagnostic => $"{diagnostic.Id}: {diagnostic.Message}")));
        Assert.Equal("123456789012345678901234567891", result.Snapshot?.Display);
    }

    [Fact]
    public async Task ExecutesExtensionsWhoseReceiverUsesAnAdditionalFrameworkReference()
    {
        var session = new ScriptSession();
        session.AddReference(typeof(FixtureConnection).Assembly.Location);
        session.AddReference(typeof(ConnectionExtensions).Assembly.Location);
        session.AddUsing("PlayGroundSharp.TestFixture");

        var result = await session.ExecuteAsync(
            1,
            "new PlayGroundSharp.TestDependency.FixtureConnection().Query<int>(\"select 1\").Count()");

        Assert.True(result.StateAccepted,
            string.Join(" | ", result.Diagnostics.Select(static diagnostic => $"{diagnostic.Id}: {diagnostic.Message}")));
        Assert.Equal("0", result.Snapshot?.Display);
    }

    [Fact]
    public async Task BoundsCompilationDiagnosticsWhilePreservingTheirTotalCount()
    {
        var code = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, 150).Select(static index => $"missing{index};"));

        var result = await new ScriptSession().ExecuteAsync(1, code);

        Assert.False(result.StateAccepted);
        Assert.Equal(100, result.Diagnostics.Count);
        Assert.True(result.TotalDiagnosticCount >= 150);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Message.Contains("missing50", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Message.Contains("missing51", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReturnsIncompleteTasksWithoutBlockingTheSession()
    {
        var session = new ScriptSession();

        var pending = await session.ExecuteAsync(1, "new TaskCompletionSource<int>().Task")
            .WaitAsync(TimeSpan.FromSeconds(2));
        var next = await session.ExecuteAsync(2, "40 + 2")
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(pending.StateAccepted);
        Assert.Equal("WaitingForActivation", pending.Snapshot?.Display);
        Assert.Equal("WaitingForActivation",
            Assert.Single(pending.Snapshot!.Properties!, static property => property.Name == "Status").Value.Display);
        Assert.Equal("42", next.Snapshot?.Display);
        Assert.Equal(2, session.Context.Submissions.Count);
    }

    [Fact]
    public async Task StreamsTaskSequenceValuesInCompletionOrder()
    {
        var streamed = new List<(int SourceIndex, ResultSnapshot Snapshot)>();
        var session = new ScriptSession();
        var firstResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = session.ExecuteAsync(
            1,
            """
            new[]
            {
                Task.Run(async () => { await Task.Delay(400); return "slow"; }),
                Task.Run(async () => { await Task.Delay(20); return "fast"; }),
                Task.Run(async () => { await Task.Delay(150); return "middle"; })
            }
            """,
            streamedResult: (sourceIndex, snapshot) =>
            {
                streamed.Add((sourceIndex, snapshot));
                firstResult.TrySetResult();
            });

        await firstResult.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(execution.IsCompleted);
        var result = await execution;

        Assert.True(result.StateAccepted);
        Assert.Null(result.Snapshot);
        Assert.Equal([1, 2, 0], streamed.Select(static item => item.SourceIndex));
        Assert.Equal(["fast", "middle", "slow"], streamed.Select(static item => item.Snapshot.Display));
        Assert.Single(session.Context.Submissions);
    }

    [Fact]
    public async Task StreamsGenericValueTaskSequenceValues()
    {
        var streamed = new List<(int SourceIndex, ResultSnapshot Snapshot)>();

        var result = await new ScriptSession().ExecuteAsync(
            1,
            """
            async ValueTask<int> Evaluate(int value, int delay)
            {
                await Task.Delay(delay);
                return value;
            }
            new[] { Evaluate(1, 200), Evaluate(2, 20) }
            """,
            streamedResult: (sourceIndex, snapshot) => streamed.Add((sourceIndex, snapshot)));

        Assert.True(result.StateAccepted);
        Assert.Null(result.Snapshot);
        Assert.Equal([1, 0], streamed.Select(static item => item.SourceIndex));
        Assert.Equal(["2", "1"], streamed.Select(static item => item.Snapshot.Display));
    }

    [Fact]
    public async Task PreservesTupleElementNamesInStreamedResults()
    {
        var streamed = new List<ResultSnapshot>();

        var result = await new ScriptSession().ExecuteAsync(
            1,
            "new[] { Task.FromResult((Name: \"Ada\", Age: 30)) }",
            streamedResult: (_, snapshot) => streamed.Add(snapshot));

        Assert.True(result.StateAccepted);
        var properties = Assert.Single(streamed).Properties;
        Assert.Equal(["Name", "Age"], properties?.Select(static property => property.Name));
        Assert.Equal(["Ada", "30"], properties?.Select(static property => property.Value.Display));
    }

    [Fact]
    public async Task RetainedStreamedResultUsesTheStableCompletedPreviewWithoutReenumerating()
    {
        var streamed = new List<(int SourceIndex, ResultSnapshot Snapshot)>();
        var session = new ScriptSession();

        var result = await session.ExecuteAsync(
            1,
            """
            var taskCreations = 0;
            Enumerable.Range(1, 100).Select(value =>
            {
                taskCreations++;
                return Task.FromResult(value);
            })
            """,
            streamedResult: (sourceIndex, snapshot) => streamed.Add((sourceIndex, snapshot)));
        var retained = Assert.Single(session.GetRetainedResults());
        var retainedAgain = Assert.Single(session.GetRetainedResults());
        var taskCreations = Assert.Single(
            session.GetVariables(),
            static variable => variable.Name == "taskCreations");

        Assert.True(result.StateAccepted);
        Assert.Equal(100, streamed.Count);
        Assert.Equal(100, retained.Value.TotalCount);
        Assert.Equal(100, retained.Value.Items?.Count);
        Assert.Equal(100, retained.Value.ItemIndexes?.Distinct().Count());
        Assert.Equal(
            Enumerable.Range(1, 100),
            retained.Value.Items!.Select(static item => int.Parse(item.Display!)).Order());
        Assert.Same(retained.Value, retainedAgain.Value);
        Assert.Equal("100", taskCreations.Value.Display);
    }

    [Fact]
    public async Task BoundsStableStreamedPreviewWhilePreservingTheCompletedResultCount()
    {
        var session = new ScriptSession();

        await session.ExecuteAsync(
            1,
            """
            Enumerable.Range(1, 100).Select(value => Task.FromResult(
                (A: value, B: value, C: value, D: value, E: value,
                 F: value, G: value, H: value, I: value, J: value)))
            """,
            streamedResult: static (_, _) => { });

        var retained = Assert.Single(session.GetRetainedResults()).Value;

        Assert.Equal(100, retained.TotalCount);
        Assert.True(retained.IsTruncated);
        Assert.InRange(retained.Items?.Count ?? 0, 1, 99);
        Assert.Equal(retained.Items?.Count, retained.ItemIndexes?.Count);
    }

    [Fact]
    public async Task StreamsFailedTaskElementsWithoutRejectingTheSubmission()
    {
        var streamed = new List<ResultSnapshot>();
        var session = new ScriptSession();

        var result = await session.ExecuteAsync(
            1,
            "new[] { Task.FromException<int>(new InvalidOperationException(\"boom\")), Task.FromResult(42) }",
            streamedResult: (_, snapshot) => streamed.Add(snapshot));
        var next = await session.ExecuteAsync(2, "40 + 2");

        Assert.True(result.StateAccepted);
        Assert.Contains(streamed, static snapshot =>
            snapshot.Kind == SnapshotKind.Exception &&
            snapshot.Display!.Contains("boom", StringComparison.Ordinal));
        Assert.Contains(streamed, static snapshot => snapshot.Display == "42");
        Assert.Equal("42", next.Snapshot?.Display);
    }

    [Fact]
    public async Task CancellingTaskSequenceEvaluationDoesNotAcceptTheSubmission()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var session = new ScriptSession();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteAsync(
            1,
            "new[] { Task.Run(async () => { await Task.Delay(10_000); return 1; }) }",
            cancellationToken: cancellation.Token,
            streamedResult: static (_, _) => { }));
        var next = await session.ExecuteAsync(1, "40 + 2");

        Assert.Equal(["40 + 2"], session.Context.Submissions);
        Assert.Equal("42", next.Snapshot?.Display);
    }

    [Fact]
    public async Task ReturnsUnevaluatedLazyValuesWithoutRunningTheirFactory()
    {
        var session = new ScriptSession();

        var pending = await session.ExecuteAsync(1, "new Lazy<int>(() => { while (true) { } })")
            .WaitAsync(TimeSpan.FromSeconds(2));
        var next = await session.ExecuteAsync(2, "40 + 2")
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(pending.StateAccepted);
        Assert.Equal("NotCreated", pending.Snapshot?.Display);
        Assert.Equal("42", next.Snapshot?.Display);
    }

    [Fact]
    public async Task DoesNotInvokeComputedPropertyGettersFromSubmittedTypes()
    {
        var session = new ScriptSession();
        var declaration = await session.ExecuteAsync(1, """
            class SnapshotSafetyValue
            {
                public int Safe { get; } = 42;
                public int Blocking { get { while (true) { } } }
            }
            """);

        var result = await session.ExecuteAsync(2, "new SnapshotSafetyValue()")
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(declaration.StateAccepted);
        Assert.True(result.StateAccepted);
        Assert.Equal("42",
            Assert.Single(result.Snapshot!.Properties!, static property => property.Name == "Safe").Value.Display);
        var blocking = Assert.Single(
            result.Snapshot.Properties!,
            static property => property.Name == "Blocking").Value;
        Assert.Equal("getter not evaluated", blocking.Display);
        Assert.True(blocking.IsTruncated);
        Assert.Contains("$playgroundSharp", SnapshotJsonFormatter.Format(blocking), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapturesAnonymousProjectionValuesWithoutInvokingGetters()
    {
        var result = await new ScriptSession().ExecuteAsync(
            1,
            "new[] { new { Id = 1, Name = \"Ada\" }, new { Id = 2, Name = \"Grace\" } }");

        Assert.True(result.StateAccepted);
        var rows = Assert.IsAssignableFrom<IReadOnlyList<ResultSnapshot>>(result.Snapshot!.Items);
        Assert.Equal(2, rows.Count);
        Assert.Equal(["1", "Ada"], rows[0].Properties!.Select(static property => property.Value.Display));
        Assert.Equal(["2", "Grace"], rows[1].Properties!.Select(static property => property.Value.Display));
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
    public async Task CancellationDuringResultCaptureDoesNotAcceptTheSubmission()
    {
        var session = new ScriptSession();
        Assert.True((await session.ExecuteAsync(1, "var retained = 21;")).StateAccepted);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteAsync(
                2,
                "Enumerable.Range(1, 10_000).Select(i => { System.Threading.Thread.SpinWait(5_000_000); return i; })",
                cancellationToken: cancellation.Token))
            .WaitAsync(TimeSpan.FromSeconds(4));

        Assert.Single(session.Context.Submissions);
        var next = await session.ExecuteAsync(2, "retained * 2");
        Assert.Equal("42", next.Snapshot?.Display);
        Assert.Equal(2, session.Context.Submissions.Count);
    }

    [Fact]
    public async Task ReportsRetainedVariablesWithoutShorteningCapturedStrings()
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
        Assert.Equal(600, longText.Value.Display?.Length);
        Assert.False(longText.Value.IsTruncated);
        var values = Assert.Single(variables, static variable => variable.Name == "values");
        Assert.Equal(10, values.Value.Items?.Count);
        Assert.Equal("10", values.Value.Items?[9].Display);
    }

    [Fact]
    public async Task ReportsUnnamedResultsAndNamesTheSameInstance()
    {
        var session = new ScriptSession();
        await session.ExecuteAsync(1, "new JsonObject { [\"name\"] = \"before\" }");

        var retained = Assert.Single(session.GetRetainedResults());
        Assert.Equal(1, retained.SubmissionIndex);
        Assert.Equal("System.Text.Json.Nodes.JsonObject", retained.TypeName);
        Assert.Equal("global::System.Text.Json.Nodes.JsonObject", retained.TypeExpression);

        var naming = await session.ExecuteAsync(
            2,
            "var json = RetainResultAs<global::System.Text.Json.Nodes.JsonObject>(1);");

        Assert.True(naming.StateAccepted);
        Assert.Empty(session.GetRetainedResults());
        var sameInstance = await session.ExecuteAsync(
            3,
            "json[\"name\"] = \"after\"; object.ReferenceEquals(json, Out[1])");
        Assert.Equal("true", sameInstance.Snapshot?.Display);
        Assert.Contains(session.GetVariables(), static variable =>
            variable.Name == "json" && variable.Value.Properties?.Single().Value.Display == "after");
    }

    [Fact]
    public async Task DoesNotDuplicateANamedValueResult()
    {
        var session = new ScriptSession();

        await session.ExecuteAsync(1, "var count = 42; count");

        Assert.Contains(session.GetVariables(), static variable => variable.Name == "count");
        Assert.Empty(session.GetRetainedResults());
    }

    [Fact]
    public async Task AnonymousResultCanBeNamedAsDynamic()
    {
        var session = new ScriptSession();
        await session.ExecuteAsync(1, "new { Name = \"Alice\" }");

        var retained = Assert.Single(session.GetRetainedResults());
        Assert.Equal("dynamic", retained.TypeExpression);

        Assert.True((await session.ExecuteAsync(
            2,
            "dynamic person = RetainResultAsDynamic(1);")).StateAccepted);
        var value = await session.ExecuteAsync(3, "person.Name");

        Assert.Equal("Alice", value.Snapshot?.Display);
        Assert.DoesNotContain(session.GetRetainedResults(), static result => result.SubmissionIndex == 1);
    }

    [Fact]
    public async Task DynamicResultUsesItsPublicRuntimeTypeWhenAvailable()
    {
        var session = new ScriptSession();
        await session.ExecuteAsync(
            1,
            "(dynamic)new JsonObject { [\"name\"] = \"Alice\" }");

        var retained = Assert.Single(session.GetRetainedResults());

        Assert.Equal("global::System.Text.Json.Nodes.JsonObject", retained.TypeExpression);
    }

    [Fact]
    public async Task JsonLoadedThroughTargetFrameworkGlobalsCanBeNamedWithAStaticType()
    {
        var framework = DotNetFrameworkLocator.Discover()
            .FirstOrDefault(candidate => candidate.Version.Major == Environment.Version.Major);
        Assert.NotNull(framework);
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "{\"name\":\"Alice\"}");
            var session = new ScriptSession(framework.GetReferencePaths(), framework.TargetFramework);
            await session.ExecuteAsync(
                1,
                $"await Data.ReadJsonAsync(@\"{path}\", ExecutionCancellation)");

            var retained = Assert.Single(session.GetRetainedResults());
            Assert.Equal("global::System.Text.Json.Nodes.JsonObject", retained.TypeExpression);

            var naming = await session.ExecuteAsync(
                2,
                "var json = RetainResultAs<global::System.Text.Json.Nodes.JsonObject>(1);");
            var value = await session.ExecuteAsync(3, "json[\"name\"]!.GetValue<string>()");

            Assert.True(naming.StateAccepted);
            Assert.Equal("Alice", value.Snapshot?.Display);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FailedNamingCastLeavesTheResultUnnamed()
    {
        var session = new ScriptSession();
        await session.ExecuteAsync(1, "new object()");

        var result = await session.ExecuteAsync(
            2,
            "var number = RetainResultAs<global::System.Int32>(1);");

        Assert.True(result.StateAccepted);
        Assert.Equal("System.InvalidCastException", result.Exception?.TypeName);
        Assert.Equal(1, Assert.Single(session.GetRetainedResults()).SubmissionIndex);
    }

    [Fact]
    public async Task ReleaseRemovesAnUnnamedResultFromHistory()
    {
        var session = new ScriptSession();
        await session.ExecuteAsync(1, "new object()");

        Assert.True((await session.ExecuteAsync(2, "ReleaseResult(1);")).StateAccepted);

        Assert.Empty(session.GetRetainedResults());
        var missing = await session.ExecuteAsync(3, "Out[1]");
        Assert.Equal("System.Collections.Generic.KeyNotFoundException", missing.Exception?.TypeName);
    }

    [Fact]
    public async Task BoundsSnapshotsAcrossTheEntireVariableList()
    {
        var session = new ScriptSession();
        var aliases = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, 48).Select(static index => $"var alias{index} = shared;"));
        var result = await session.ExecuteAsync(1, $$"""
            var shared = Enumerable.Repeat(new string('x', 512), 512).ToArray();
            {{aliases}}
            """);

        var variables = session.GetVariables();

        Assert.True(result.StateAccepted);
        Assert.Equal(49, variables.Count);
        Assert.Equal(SnapshotKind.Sequence,
            Assert.Single(variables, static variable => variable.Name == "shared").Value.Kind);
        Assert.Contains(variables, static variable =>
            variable.Value.Kind == SnapshotKind.MaxDepth && variable.Value.IsTruncated);
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
    public async Task ReadsOrdinaryJsonAsJsonNodeFromSessionGlobals()
    {
        var path = Path.Combine(Path.GetTempPath(), $"PlayGroundSharp-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """{"name":"Ada","scores":[10,20]}""");
        try
        {
            var escapedPath = path.Replace("\"", "\"\"");
            var session = new ScriptSession();
            var loaded = await session.ExecuteAsync(
                1,
                $"var json = await Data.ReadJsonAsync(@\"{escapedPath}\"); json");
            var property = await session.ExecuteAsync(2, """json!["name"]!.GetValue<string>()""");

            Assert.True(loaded.StateAccepted);
            Assert.Equal("System.Text.Json.Nodes.JsonObject", loaded.Snapshot?.TypeName);
            Assert.Equal(["name", "scores"], loaded.Snapshot?.Properties?.Select(static item => item.Name));
            Assert.True(property.StateAccepted);
            Assert.Equal("Ada", property.Snapshot?.Display);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DataInferenceInspectsPropertiesAcrossSeventyThousandJsonRows()
    {
        var session = new ScriptSession();
        var declaration = await session.ExecuteAsync(1, """
            var rows = new JsonArray();
            for (var index = 0; index < 70_000; index++)
                rows.Add(index == 69_999
                    ? new JsonObject { ["id"] = index, ["lateProperty"] = "found" }
                    : new JsonObject { ["id"] = index });
            """);

        var inspection = await session.InspectExpressionAsync("rows", forDataInference: true);

        Assert.True(declaration.StateAccepted);
        var row = Assert.Single(inspection.Snapshot!.Items!);
        Assert.Contains(row.Properties!, property => property.Name == "lateProperty" && property.IsOptional);
        Assert.Equal(70_000, inspection.Snapshot.TotalCount);
    }

    [Fact]
    public async Task JsonArrayPreviewSnapshotReportsUncapturedItems()
    {
        var path = Path.Combine(Path.GetTempPath(), $"PlayGroundSharp-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "[1,2,3]");
        try
        {
            var escapedPath = path.Replace("\"", "\"\"");
            var result = await new ScriptSession().ExecuteAsync(
                1,
                $"await Data.ReadJsonArrayAsync(@\"{escapedPath}\", 2)");

            Assert.True(result.StateAccepted);
            Assert.True(result.Snapshot?.IsTruncated);
            Assert.Null(result.Snapshot?.TotalCount);
            Assert.Equal(2, result.Snapshot?.Items?.Count);
            Assert.Equal("2 captured items", result.Snapshot?.Display);
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

    [Fact]
    public async Task JsonLinesPreviewSnapshotReportsUncapturedItems()
    {
        var path = Path.Combine(Path.GetTempPath(), $"PlayGroundSharp-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllTextAsync(path, "1\n2\n3\n");
        try
        {
            var result = await new ScriptSession().ExecuteAsync(
                1,
                DataSnippetBuilder.CreateJsonLines(path, 2));

            Assert.True(result.StateAccepted);
            Assert.True(result.Snapshot?.IsTruncated);
            Assert.Null(result.Snapshot?.TotalCount);
            Assert.Equal(2, result.Snapshot?.Items?.Count);
            Assert.Equal("2 captured items", result.Snapshot?.Display);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExecutesGeneratedMixedJsonAndJsonLinesSnippet()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PlayGroundSharp-JsonBatch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var paths = new[]
        {
            Path.Combine(directory, "one.json"),
            Path.Combine(directory, "two.jsonl"),
            Path.Combine(directory, "three.ndjson")
        };
        await File.WriteAllTextAsync(paths[0], "{\"id\":1}");
        await File.WriteAllTextAsync(paths[1], "{\"id\":2}\n{\"id\":3}\n");
        await File.WriteAllTextAsync(paths[2], "{\"id\":4}\n");
        try
        {
            var result = await new ScriptSession().ExecuteAsync(
                1,
                DataSnippetBuilder.CreateJsonFilesBatch(paths));

            Assert.True(result.StateAccepted,
                string.Join(" | ", result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            Assert.Equal(4, result.Snapshot?.TotalCount);
            Assert.Equal(
                ["1", "2", "3", "4"],
                result.Snapshot?.Items?.Select(static item =>
                    item.Properties?.Single(property => property.Name == "id").Value.Display));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExecutesGeneratedCompleteFileReadingSnippets()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PlayGroundSharp-FileModes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var jsonPath = Path.Combine(directory, "items.data");
        var jsonLinesPath = Path.Combine(directory, "events.data");
        var textPath = Path.Combine(directory, "text.data");
        await File.WriteAllTextAsync(jsonPath, "{\"id\":1}");
        await File.WriteAllTextAsync(jsonLinesPath, "{\"id\":2}\n{\"id\":3}\n");
        await File.WriteAllTextAsync(textPath, "alpha\nbeta");
        try
        {
            var session = new ScriptSession();

            var json = await session.ExecuteAsync(1, DataSnippetBuilder.CreateJson(jsonPath));
            var jsonLines = await session.ExecuteAsync(2, DataSnippetBuilder.CreateAllJsonLines(jsonLinesPath));
            var text = await session.ExecuteAsync(3, DataSnippetBuilder.CreateAllText(textPath));
            var lines = await session.ExecuteAsync(4, DataSnippetBuilder.CreateLineStream(textPath));
            var bytes = await session.ExecuteAsync(5, DataSnippetBuilder.CreateAllBytes(textPath));
            var jsonLineFiles = await session.ExecuteAsync(6,
                DataSnippetBuilder.CreateJsonLinesBatch([jsonLinesPath]));
            var textFiles = await session.ExecuteAsync(7,
                DataSnippetBuilder.CreateTextBatch([textPath]));
            var lineStreams = await session.ExecuteAsync(8,
                DataSnippetBuilder.CreateLineStreams([textPath]));
            var binaryFiles = await session.ExecuteAsync(9,
                DataSnippetBuilder.CreateBytesBatch([textPath]));

            Assert.All(
                [json, jsonLines, text, lines, bytes, jsonLineFiles, textFiles, lineStreams, binaryFiles],
                result =>
                Assert.True(result.StateAccepted,
                    string.Join(" | ", result.Diagnostics.Select(static diagnostic => diagnostic.Message))));
            Assert.Equal(2, jsonLines.Snapshot?.TotalCount);
            Assert.Equal("alpha\nbeta", text.Snapshot?.Display);
            Assert.Equal(2, lines.Snapshot?.TotalCount);
            Assert.Equal(10, bytes.Snapshot?.TotalCount);
            Assert.All([jsonLineFiles, textFiles, lineStreams, binaryFiles], result =>
                Assert.Equal(1, result.Snapshot?.TotalCount));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
