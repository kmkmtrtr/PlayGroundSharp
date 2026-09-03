using PlayGroundSharp.Core;
using PlayGroundSharp.LanguageService;
using PlayGroundSharp.Worker;

namespace PlayGroundSharp.App.Tests;

public sealed class TypedResultCompletionTests
{
    [Fact]
    public async Task NamedRetainedResultKeepsItsWorkerTypeForCompletion()
    {
        var framework = DotNetFrameworkLocator.Discover()
            .First(candidate => candidate.Version.Major == Environment.Version.Major);
        var frameworkReferences = framework.GetReferencePaths();
        var session = new ScriptSession(frameworkReferences, framework.TargetFramework);
        var source = await session.ExecuteAsync(
            1,
            "new JsonObject { [\"name\"] = \"Alice\" }");
        var retained = Assert.Single(session.GetRetainedResults());

        var namingCode = RetainedResultStatement.Name(
            retained.SubmissionIndex,
            retained.TypeExpression,
            "json");
        var naming = await session.ExecuteAsync(2, namingCode);
        var completionContext = session.Context with
        {
            FrameworkReferencePaths = frameworkReferences
        };
        var service = new CSharpLanguageService();
        var completions = await service.GetCompletionsAsync(
            completionContext,
            "json.",
            "json.".Length);

        Assert.True(source.StateAccepted);
        Assert.Equal("global::System.Text.Json.Nodes.JsonObject", retained.TypeExpression);
        Assert.StartsWith(retained.TypeExpression + " json = ", namingCode, StringComparison.Ordinal);
        Assert.True(naming.StateAccepted,
            string.Join(" | ", naming.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var variable = Assert.Single(session.GetVariables(), static variable => variable.Name == "json");
        Assert.Equal("System.Text.Json.Nodes.JsonObject", variable.TypeName);
        Assert.Contains(completions, static candidate => candidate.DisplayText == "TryGetPropertyValue");
        Assert.Contains(completions, static candidate => candidate.DisplayText == "Add");
        Assert.Contains(completions, static candidate => candidate.DisplayText == "ToJsonString");
    }

    [Fact]
    public async Task OutDerivedVariablesUseRuntimeTypesAndCompileTimeTupleNamesForCompletion()
    {
        var framework = DotNetFrameworkLocator.Discover()
            .First(candidate => candidate.Version.Major == Environment.Version.Major);
        var frameworkReferences = framework.GetReferencePaths();
        var session = new ScriptSession(frameworkReferences, framework.TargetFramework);
        await session.ExecuteAsync(1, "(index: 1, delay: 20)");
        await session.ExecuteAsync(2, "var pair = Out[1];");
        await session.ExecuteAsync(3, "new JsonObject { [\"name\"] = \"Alice\" }");
        await session.ExecuteAsync(4, "var json = Out[3];");
        var variables = session.GetVariables();
        var context = session.Context with
        {
            FrameworkReferencePaths = frameworkReferences,
            VariableTypeHints = variables
                .Where(static variable => variable.CompletionTypeExpression is not null)
                .Select(static variable => new VariableTypeHint(
                    variable.Name,
                    variable.CompletionTypeExpression!))
                .ToArray()
        };
        var service = new CSharpLanguageService();

        var pairCompletions = await service.GetCompletionsAsync(context, "pair.", "pair.".Length);
        var jsonCompletions = await service.GetCompletionsAsync(context, "json.", "json.".Length);

        Assert.Contains(pairCompletions, static candidate => candidate.DisplayText == "index");
        Assert.Contains(pairCompletions, static candidate => candidate.DisplayText == "delay");
        Assert.DoesNotContain(pairCompletions, static candidate => candidate.DisplayText == "Item1");
        Assert.Contains(jsonCompletions, static candidate => candidate.DisplayText == "TryGetPropertyValue");
        Assert.Contains(jsonCompletions, static candidate => candidate.DisplayText == "ToJsonString");
    }

    [Fact]
    public async Task NamedTupleResultKeepsElementNamesForCompletion()
    {
        var framework = DotNetFrameworkLocator.Discover()
            .First(candidate => candidate.Version.Major == Environment.Version.Major);
        var frameworkReferences = framework.GetReferencePaths();
        var session = new ScriptSession(frameworkReferences, framework.TargetFramework);
        await session.ExecuteAsync(1, "(index: 1, delay: 20)");
        var retained = Assert.Single(session.GetRetainedResults());

        var namingCode = RetainedResultStatement.Name(
            retained.SubmissionIndex,
            retained.TypeExpression,
            "pair");
        var naming = await session.ExecuteAsync(2, namingCode);
        var context = session.Context with
        {
            FrameworkReferencePaths = frameworkReferences
        };
        var service = new CSharpLanguageService();
        var completions = await service.GetCompletionsAsync(context, "pair.", "pair.".Length);

        Assert.True(naming.StateAccepted,
            string.Join(" | ", naming.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.Contains("index", namingCode, StringComparison.Ordinal);
        Assert.Contains("delay", namingCode, StringComparison.Ordinal);
        Assert.Contains(completions, static candidate => candidate.DisplayText == "index");
        Assert.Contains(completions, static candidate => candidate.DisplayText == "delay");
        Assert.DoesNotContain(completions, static candidate => candidate.DisplayText == "Item1");
    }
}
