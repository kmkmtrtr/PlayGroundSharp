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
    public async Task CompletesLargeDataHelpers()
    {
        const string code = "Data.";

        var items = await service.GetCompletionsAsync(SessionContext.Empty, code, code.Length);

        Assert.Contains(items, static item => item.DisplayText == "PreviewText");
        Assert.Contains(items, static item => item.DisplayText == "ReadLines");
        Assert.Contains(items, static item => item.DisplayText == "ReadJsonAsync");
        Assert.Contains(items, static item => item.DisplayText == "ReadJsonArrayAsync");
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
    public async Task CompletesInnerExpressionInsideMethodArguments()
    {
        var context = new SessionContext(["var innerText = \"hello\""], SessionContext.DefaultImports, []);
        const string code = "Console.WriteLine(innerText.";

        var items = await service.GetCompletionsAsync(context, code, code.Length);

        Assert.Contains(items, static item => item.DisplayText == "Length");
        Assert.Contains(items, static item => item.DisplayText == "Contains");
        Assert.DoesNotContain(items, static item => item.DisplayText == "WriteLine");
    }

    [Fact]
    public async Task DoesNotOfferInstanceExtensionsAfterATypeName()
    {
        var context = new SessionContext(
            [],
            [.. SessionContext.DefaultImports, "PlayGroundSharp.TestFixture"],
            [typeof(NumberExtensions).Assembly.Location]);

        var typeItems = await service.GetCompletionsAsync(context, "int.", "int.".Length);
        var instanceItems = await service.GetCompletionsAsync(context, "(1).", "(1).".Length);

        Assert.DoesNotContain(typeItems, static item => item.DisplayText == "Billions");
        var extension = Assert.Single(instanceItems, static item => item.DisplayText == "Billions");
        Assert.True(extension.IsExtensionMethod);
        Assert.Contains("PlayGroundSharp.TestFixture", extension.NamespaceHint, StringComparison.Ordinal);
        var orderedItems = instanceItems.ToList();
        var instanceMemberIndex = orderedItems.FindIndex(static item => item.DisplayText == "ToString");
        Assert.True(instanceMemberIndex >= 0);
        Assert.True(instanceMemberIndex < orderedItems.IndexOf(extension));
    }

    [Fact]
    public async Task ParenthesizesNumericLiteralWhenCompletingAMember()
    {
        var context = new SessionContext(
            [],
            [.. SessionContext.DefaultImports, "PlayGroundSharp.TestFixture"],
            [typeof(NumberExtensions).Assembly.Location]);

        var items = await service.GetCompletionsAsync(context, "1.Bil", "1.Bil".Length);
        var extension = Assert.Single(items, static item => item.DisplayText == "Billions");
        var description = await service.GetCompletionDescriptionAsync(context, "1.Bil", "1.Bil".Length, extension);

        Assert.Equal(0, extension.ReplacementStart);
        Assert.Equal("(1).Billions", extension.TextToInsert);
        Assert.Contains("billions", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MarksTheNamespaceRequiredByAnUnimportedExtension()
    {
        var context = new SessionContext(
            ["var hoge = 1;"],
            SessionContext.DefaultImports,
            [typeof(NumberExtensions).Assembly.Location]);

        var items = await service.GetCompletionsAsync(context, "hoge.", "hoge.".Length);
        var extension = Assert.Single(items, static item => item.DisplayText == "Billions");

        Assert.Equal("PlayGroundSharp.TestFixture", extension.NamespaceHint);
        Assert.Equal("PlayGroundSharp.TestFixture", extension.RequiredNamespace);
        Assert.True(extension.RequiresImport);
        Assert.Equal("using PlayGroundSharp.TestFixture", extension.NamespaceDisplayText);
        Assert.Contains("using PlayGroundSharp.TestFixture", extension.AccessibleDisplayText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShowsTheNamespaceWithoutRequiringAnImportForAnActiveExtension()
    {
        var context = new SessionContext(
            ["var hoge = 1;"],
            [.. SessionContext.DefaultImports, "PlayGroundSharp.TestFixture"],
            [typeof(NumberExtensions).Assembly.Location]);

        var items = await service.GetCompletionsAsync(context, "hoge.", "hoge.".Length);
        var extension = Assert.Single(items, static item => item.DisplayText == "Billions");

        Assert.False(extension.RequiresImport);
        Assert.Equal("PlayGroundSharp.TestFixture", extension.NamespaceDisplayText);
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
        Assert.All(signature.Signatures, static item => Assert.True(item.ActiveParameter is -1 or 0));
        Assert.Contains(diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public async Task TracksActiveSignatureParameterFromCaretPosition()
    {
        var context = new SessionContext(
            ["int Combine(int first, int second, int third, int fourth) => first + second + third + fourth"],
            SessionContext.DefaultImports,
            []);
        const string code = "Combine(10, 20, 30, 40)";

        foreach (var (text, expectedParameter) in new[] { ("10", 0), ("30", 2), ("40", 3), ("20", 1) })
        {
            var help = await service.GetSignatureHelpAsync(
                context, code, code.IndexOf(text, StringComparison.Ordinal) + 1);

            Assert.NotNull(help);
            Assert.Equal(expectedParameter, help.Signatures[help.SelectedSignature].ActiveParameter);
        }
    }

    [Fact]
    public async Task ReturnsActiveParameterDocumentation()
    {
        const string code = "\"text\".Contains(\"value\")";

        var help = await service.GetSignatureHelpAsync(
            SessionContext.Empty, code, code.IndexOf("value", StringComparison.Ordinal) + 2);

        Assert.NotNull(help);
        var signature = help.Signatures[help.SelectedSignature];
        var parameter = signature.Parameters[signature.ActiveParameter];
        Assert.Equal("value", parameter.Name);
        Assert.NotEmpty(parameter.Summary);
    }

    [Fact]
    public async Task SelectsOverloadCompatibleWithEnteredArguments()
    {
        var context = new SessionContext(
            [
                "string FormatValue(int value, string suffix) => value + suffix",
                "string FormatValue(string value, bool trim) => trim ? value.Trim() : value"
            ],
            SessionContext.DefaultImports,
            []);
        const string code = "FormatValue(42, ";

        var help = await service.GetSignatureHelpAsync(context, code, code.Length);

        Assert.NotNull(help);
        var selected = help.Signatures[help.SelectedSignature];
        Assert.Contains("int value", selected.DisplayText, StringComparison.Ordinal);
        Assert.Contains("string suffix", selected.DisplayText, StringComparison.Ordinal);
        Assert.Equal("suffix", selected.Parameters[selected.ActiveParameter].Name);
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
        Assert.Equal("Greeter", candidate.DisplayText);
        Assert.Equal("using PlayGroundSharp.TestFixture", candidate.NamespaceDisplayText);
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
                "interface IEntity { }\nclass EntityBase { }\nclass Customer : EntityBase, IEntity { }",
                "/// <summary>Checks whether a user is an adult.</summary>\n/// <param name=\"user\">The user to inspect.</param>\nbool IsAdult(User user) => true"
            ],
            SessionContext.DefaultImports,
            [typeof(Greeter).Assembly.Location]);

        var entries = await service.GetSymbolExplorerAsync(context);

        Assert.Contains(entries, static entry => entry.Namespace == "System" && entry.Name == "String" && entry.Kind == "class");
        Assert.Contains(entries, static entry => entry.Namespace == "System.Linq" && entry.Name == "Enumerable" && entry.Kind == "class");
        Assert.Contains(entries, static entry => entry.Namespace == "(session)" && entry.Name == "User" && entry.Kind == "record");
        Assert.Contains(entries, static entry => entry.Namespace == "(session)" && entry.Name == "Transformer" && entry.Kind == "delegate");
        var inheritedClass = Assert.Single(entries, static entry =>
            entry.Namespace == "(session)" && entry.Name == "Customer" && entry.Kind == "class");
        Assert.Equal(["EntityBase", "IEntity"], inheritedClass.InheritedTypes);
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
