using PlayGroundSharp.Core;
using PlayGroundSharp.Worker;

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
}
