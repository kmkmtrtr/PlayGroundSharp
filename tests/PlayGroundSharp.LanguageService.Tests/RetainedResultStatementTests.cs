using PlayGroundSharp.LanguageService;

namespace PlayGroundSharp.LanguageService.Tests;

public sealed class RetainedResultStatementTests
{
    [Theory]
    [InlineData("json")]
    [InlineData("_result2")]
    [InlineData("変数")]
    [InlineData("@class")]
    public void AcceptsCSharpIdentifiers(string name) =>
        Assert.True(RetainedResultStatement.IsValidVariableName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("class")]
    [InlineData("value; Environment.Exit(0)")]
    public void RejectsInvalidOrInjectableNames(string? name) =>
        Assert.False(RetainedResultStatement.IsValidVariableName(name));

    [Fact]
    public void CreatesTypedNamingSubmission() =>
        Assert.Equal(
            "global::System.Text.Json.Nodes.JsonNode? json = RetainResultAs<global::System.Text.Json.Nodes.JsonNode?>(3);",
            RetainedResultStatement.Name(3, "global::System.Text.Json.Nodes.JsonNode?", "json"));

    [Fact]
    public void FallsBackToDynamicForAnonymousResults() =>
        Assert.Equal(
            "dynamic item = RetainResultAsDynamic(4);",
            RetainedResultStatement.Name(4, "dynamic", "item"));

    [Fact]
    public void CreatesReleaseSubmission() =>
        Assert.Equal("ReleaseResult(5);", RetainedResultStatement.Release(5));

    [Theory]
    [InlineData("value", "value", true)]
    [InlineData("@value", "value", true)]
    [InlineData("value", "Value", false)]
    [InlineData("value", "not valid", false)]
    public void ComparesIdentifierValues(string left, string right, bool expected) =>
        Assert.Equal(expected, RetainedResultStatement.RepresentsSameIdentifier(left, right));
}
