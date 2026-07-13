using PlayGroundSharp.Core;
using PlayGroundSharp.Worker;
using PlayGroundSharp.TestFixture;
using PlayGroundSharp.TestDependency;

namespace PlayGroundSharp.Worker.Tests;

[CollectionDefinition("Console", DisableParallelization = true)]
public sealed class ConsoleCollection;

[Collection("Console")]
public sealed class ScriptSessionTests
{
    [Fact]
    public async Task EvaluatesExpression()
    {
        var result = await new ScriptSession().ExecuteAsync(1, "1 + 2");
        Assert.True(result.StateAccepted);
        Assert.Equal("3", result.Snapshot?.Display);
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
    public async Task ReportsRetainedVariablesWithBoundedSnapshots()
    {
        var session = new ScriptSession();
        await session.ExecuteAsync(1, "var number = 42; const string label = \"answer\"; var longText = new string('x', 600);");
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
    public async Task AddsLocalDllReferences()
    {
        var session = new ScriptSession();
        session.AddReference(typeof(DependencyValue).Assembly.Location);
        session.AddReference(typeof(Greeter).Assembly.Location);
        var result = await session.ExecuteAsync(1, "PlayGroundSharp.TestFixture.Greeter.Message");
        Assert.Equal("hello from fixture", result.Snapshot?.Display);
    }
}
