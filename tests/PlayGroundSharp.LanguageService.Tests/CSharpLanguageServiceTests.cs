using PlayGroundSharp.Core;
using PlayGroundSharp.LanguageService;
using PlayGroundSharp.TestFixture;

namespace PlayGroundSharp.LanguageService.Tests;

public sealed class CSharpLanguageServiceTests
{
    private readonly CSharpLanguageService service = new();

    [Fact]
    public async Task CompletesSessionVariablesArrayAndLinqMembers()
    {
        var context = new SessionContext(["var values = Enumerable.Range(1, 10).ToArray();"], SessionContext.DefaultImports, []);
        var items = await service.GetCompletionsAsync(context, "values.", "values.".Length);
        var names = items.Select(static item => item.DisplayText).ToHashSet(StringComparer.Ordinal);
        var diagnostics = await service.GetDiagnosticsAsync(context, "values.");

        Assert.True(names.Contains("Length"), string.Join(" | ", diagnostics.Select(static item => $"{item.Id}: {item.Message}")));
        Assert.Contains("Where", names);
        Assert.Contains("Select", names);
        Assert.Contains("Sum", names);
        Assert.Contains("ToArray", names);
    }

    [Fact]
    public async Task CompletesMembersAfterSemicolonlessSubmission()
    {
        var context = new SessionContext(["var value = \"fuga\""], SessionContext.DefaultImports, []);

        var items = await service.GetCompletionsAsync(context, "value.", "value.".Length);

        Assert.Contains(items, static item => item.DisplayText == "Length");
        Assert.Contains(items, static item => item.DisplayText == "Contains");
    }

    [Fact]
    public async Task CompletesDefinedType()
    {
        var context = new SessionContext(["record User(string Name, int Age)"], SessionContext.DefaultImports, []);
        var items = await service.GetCompletionsAsync(context, "new Us", "new Us".Length);
        Assert.Contains(items, static item => item.DisplayText == "User");
    }

    [Fact]
    public async Task CompletesMembersFromSemicolonlessMethodAndType()
    {
        var context = new SessionContext(
            ["record Entry(string Value)", "Entry Create(string value) => new(value)"],
            SessionContext.DefaultImports,
            []);

        var items = await service.GetCompletionsAsync(context, "Create(\"fuga\").", "Create(\"fuga\").".Length);

        Assert.Contains(items, static item => item.DisplayText == "Value");
    }

    [Fact]
    public async Task ReturnsSignatureHelpAndDiagnostics()
    {
        var signature = await service.GetSignatureHelpAsync(SessionContext.Empty, "string.Join(", "string.Join(".Length);
        var diagnostics = await service.GetDiagnosticsAsync(SessionContext.Empty, "unknownName + 1");

        Assert.NotNull(signature);
        Assert.Contains(signature.Signatures, static item => item.DisplayText.Contains("Join", StringComparison.Ordinal));
        Assert.Contains(signature.Signatures, static item => !string.IsNullOrWhiteSpace(item.Summary));
        Assert.Contains(diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public async Task ReturnsDescriptionForCompletionCandidate()
    {
        const string code = "string.Empty.";
        var items = await service.GetCompletionsAsync(SessionContext.Empty, code, code.Length);
        var candidate = Assert.Single(items, static item => item.DisplayText == "Contains");

        var description = await service.GetCompletionDescriptionAsync(
            SessionContext.Empty, code, code.Length, candidate);

        Assert.Contains("Contains", description, StringComparison.Ordinal);
        Assert.Contains("specified", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompletesTypeFromDynamicReference()
    {
        var context = new SessionContext([], SessionContext.DefaultImports, [typeof(Greeter).Assembly.Location]);
        var items = await service.GetCompletionsAsync(context, "new PlayGroundSharp.TestFixture.Gre", "new PlayGroundSharp.TestFixture.Gre".Length);
        Assert.Contains(items, static item => item.DisplayText == "Greeter");
    }

    [Fact]
    public async Task SuggestsUnimportedTypeFromDynamicReference()
    {
        var context = new SessionContext([], SessionContext.DefaultImports, [typeof(Greeter).Assembly.Location]);
        const string code = "new Gree";

        var items = await service.GetCompletionsAsync(context, code, code.Length);

        var candidate = Assert.Single(items, static item =>
            item.TextToInsert == "Greeter" && item.RequiredNamespace == "PlayGroundSharp.TestFixture");
        Assert.Contains("PlayGroundSharp.TestFixture", candidate.DisplayText, StringComparison.Ordinal);
        Assert.DoesNotContain(items, static item => item.TextToInsert == "Greeter" && item.RequiredNamespace is null);
    }

    [Fact]
    public void ReturnsNamespacesFromDynamicReferences()
    {
        var context = new SessionContext([], SessionContext.DefaultImports, [typeof(Greeter).Assembly.Location]);

        var namespaces = service.GetReferenceNamespaces(context);

        Assert.Contains("PlayGroundSharp.TestFixture", namespaces);
    }

    [Fact]
    public async Task BuildsTypeExplorerFromImportsSessionAndDynamicReferences()
    {
        var context = new SessionContext(
            [
                "record User(string Name)",
                "delegate int Transformer(int value)",
                "/// <summary>Checks whether a user is an adult.</summary>\n/// <param name=\"user\">The user to inspect.</param>\nbool IsAdult(User user) => true"
            ],
            SessionContext.DefaultImports,
            [typeof(Greeter).Assembly.Location]);

        var entries = await service.GetSymbolExplorerAsync(context);

        Assert.Contains(entries, static entry => entry.Namespace == "System" && entry.Name == "String" && entry.Kind == "class");
        Assert.Contains(entries, static entry => entry.Namespace == "System.Linq" && entry.Name == "Enumerable" && entry.Kind == "class");
        Assert.Contains(entries, static entry => entry.Namespace == "(session)" && entry.Name == "User" && entry.Kind == "record");
        Assert.Contains(entries, static entry => entry.Namespace == "(session)" && entry.Name == "Transformer" && entry.Kind == "delegate");
        Assert.Contains(entries, static entry =>
            entry.Namespace == "PlayGroundSharp.TestFixture" && entry.Name == "Greeter");

        var sessionMethod = Assert.Single(entries, static entry =>
            entry.Namespace == "(session)" && entry.Name == "IsAdult" && entry.Kind == "method");
        Assert.Contains("adult", sessionMethod.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("The user to inspect.", Assert.Single(sessionMethod.Parameters).Summary);

        var frameworkMethod = Assert.Single(entries, static entry =>
            entry.Namespace == "System" && entry.ContainingType == "String" &&
            entry.Name == "Contains" && entry.Parameters.Count == 1 && entry.Parameters[0].TypeName == "string");
        Assert.NotEmpty(frameworkMethod.Summary);
        Assert.NotEmpty(frameworkMethod.Parameters[0].Summary);

        var dynamicMethod = Assert.Single(entries, static entry =>
            entry.Namespace == "PlayGroundSharp.TestFixture" && entry.ContainingType == "Greeter" && entry.Name == "Greet");
        Assert.Equal("Creates a greeting for the specified person.", dynamicMethod.Summary);
        Assert.Equal("The person to greet.", Assert.Single(dynamicMethod.Parameters).Summary);
    }
}
