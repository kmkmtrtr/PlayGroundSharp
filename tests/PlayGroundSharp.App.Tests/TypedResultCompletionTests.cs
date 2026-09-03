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
}
