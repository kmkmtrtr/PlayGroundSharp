using PlayGroundSharp.Core;
using PlayGroundSharp.LanguageService;
using PlayGroundSharp.TestFixture;

namespace PlayGroundSharp.LanguageService.Tests;

public sealed class CSharpLanguageServiceTests
{
    private readonly CSharpLanguageService service = new();

    [Theory]
    [InlineData("var value = 42", false)]
    [InlineData("Enumerable.Range(1, 10).Sum()", false)]
    [InlineData("record Customer(string Name)", true)]
    [InlineData("enum Status { Ready, Busy }", true)]
    [InlineData("int Twice(int value) => value * 2", true)]
    public void DetectsSubmissionsThatCanChangeTheSymbolExplorer(string code, bool expected)
    {
        Assert.Equal(expected, CSharpLanguageService.ContainsSymbolExplorerDeclarations(code));
    }

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
    public async Task AcceptsDynamicMemberBindingInDiagnostics()
    {
        const string code = "dynamic value = new { Number = 21 }; (int)value.Number * 2";

        var diagnostics = await service.GetDiagnosticsAsync(SessionContext.Empty, code);

        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public async Task AcceptsExtendedFrameworkNumericTypesInDiagnostics()
    {
        const string code = "System.Numerics.BigInteger.Parse(\"123456789012345678901234567890\") + 1";

        var diagnostics = await service.GetDiagnosticsAsync(SessionContext.Empty, code);

        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public async Task DoesNotExposeLargeDataImplementationTypes()
    {
        const string code = "new Bounded";

        var items = await service.GetCompletionsAsync(SessionContext.Empty, code, code.Length);

        Assert.DoesNotContain(items, static item => item.DisplayText == "BoundedJsonElementList");
        Assert.DoesNotContain(items, static item => item.DisplayText == "IBoundedSequenceResult");
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
    public async Task DoesNotOfferExtensionsFromLanguageServiceImplementationDependencies()
    {
        var items = await service.GetCompletionsAsync(SessionContext.Empty, "(1).", "(1).".Length);

        Assert.DoesNotContain(items, static item => item.DisplayText == "Billions");
    }

    [Fact]
    public async Task OffersHumanizerExtensionsAfterItsAssemblyIsAddedToTheSession()
    {
        var humanizerPath = Path.Combine(AppContext.BaseDirectory, "Humanizer.dll");
        Assert.True(File.Exists(humanizerPath));
        var context = new SessionContext([], SessionContext.DefaultImports, [humanizerPath]);

        var items = await service.GetCompletionsAsync(context, "(1).", "(1).".Length);
        var extension = Assert.Single(items, static item => item.DisplayText == "Billions");
        var requiredImports = await service.GetRequiredExtensionImportsAsync(context, "(1).Billions()");

        Assert.Equal("Humanizer", extension.RequiredNamespace);
        Assert.Equal(["Humanizer"], requiredImports);
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
    public async Task MarksTheNamespaceRequiredByAnUnimportedExtensionAfterTypingPrefix()
    {
        var context = new SessionContext(
            ["var hoge = 1;"],
            SessionContext.DefaultImports,
            [typeof(NumberExtensions).Assembly.Location]);

        const string code = "hoge.Bil";
        var items = await service.GetCompletionsAsync(context, code, code.Length);
        var extension = Assert.Single(items, static item => item.DisplayText == "Billions");

        Assert.Equal("PlayGroundSharp.TestFixture", extension.NamespaceHint);
        Assert.Equal("PlayGroundSharp.TestFixture", extension.RequiredNamespace);
        Assert.True(extension.RequiresImport);
        Assert.Equal("using PlayGroundSharp.TestFixture", extension.NamespaceDisplayText);
        Assert.Contains("using PlayGroundSharp.TestFixture", extension.AccessibleDisplayText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindsRequiredImportForManuallyTypedExtensionInvocation()
    {
        var context = new SessionContext(
            ["var hoge = 1;"],
            SessionContext.DefaultImports,
            [typeof(NumberExtensions).Assembly.Location]);

        var requiredImports = await service.GetRequiredExtensionImportsAsync(context, "hoge.Billions()");

        Assert.Equal(["PlayGroundSharp.TestFixture"], requiredImports);
    }

    [Fact]
    public async Task DoesNotRequireImportForAnAlreadyActiveOrUnknownMethod()
    {
        var context = new SessionContext(
            ["var hoge = 1;"],
            [.. SessionContext.DefaultImports, "PlayGroundSharp.TestFixture"],
            [typeof(NumberExtensions).Assembly.Location]);

        var activeImports = await service.GetRequiredExtensionImportsAsync(context, "hoge.Billions()");
        var unknownImports = await service.GetRequiredExtensionImportsAsync(
            context with { Imports = SessionContext.DefaultImports },
            "hoge.NotARealMethod()");
        var typeReceiverImports = await service.GetRequiredExtensionImportsAsync(
            context with { Imports = SessionContext.DefaultImports },
            "int.Billions()");

        Assert.Empty(activeImports);
        Assert.Empty(unknownImports);
        Assert.Empty(typeReceiverImports);
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
        Assert.All(signature.Signatures,
            static item => Assert.Equal(item.DisplayText, item.AccessibleDisplayText));
        Assert.Contains(diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Theory]
    [InlineData("1 + 2")]
    [InlineData("delayed + 1")]
    [InlineData("var next = delayed + 1")]
    public async Task DoesNotReportErrorsForValidTrailingExpressions(string currentCode)
    {
        var context = new SessionContext(["var delayed = 42;"], SessionContext.DefaultImports, []);

        var diagnostics = await service.GetDiagnosticsAsync(context, currentCode);

        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public async Task KeepsMissingSemicolonDiagnosticsInsideTheCurrentSubmission()
    {
        const string code = "var first = 1\nvar second = 2;";

        var diagnostics = await service.GetDiagnosticsAsync(SessionContext.Empty, code);

        Assert.Contains(diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public async Task ReportsDiagnosticPositionsRelativeToTheCurrentSubmission()
    {
        var context = new SessionContext(
            ["var previous = 42;", "var multiLine = new[]\n{\n    previous\n};"],
            SessionContext.DefaultImports,
            []);
        const string code = "var current = missingName;\ncurrent + anotherMissing";

        var diagnostics = await service.GetDiagnosticsAsync(context, code);

        Assert.Contains(diagnostics, static diagnostic =>
            diagnostic.Message.Contains("missingName", StringComparison.Ordinal) &&
            diagnostic.StartLine == 1 && diagnostic.StartColumn == 15);
        Assert.Contains(diagnostics, static diagnostic =>
            diagnostic.Message.Contains("anotherMissing", StringComparison.Ordinal) &&
            diagnostic.StartLine == 2 && diagnostic.StartColumn == 11);
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
    public async Task ReturnsQuickInfoAtTheCaretAfterPreviousSubmissions()
    {
        var context = new SessionContext(["var sessionText = \"hello\";"], SessionContext.DefaultImports, []);
        const string code = "sessionText.Length";

        var quickInfo = await service.GetQuickInfoAsync(
            context,
            code,
            code.IndexOf("Length", StringComparison.Ordinal) + 2);

        Assert.NotNull(quickInfo);
        Assert.Contains("Length", quickInfo.Text, StringComparison.Ordinal);
        Assert.Contains("int", quickInfo.Text, StringComparison.OrdinalIgnoreCase);
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
    public async Task SuggestsUnimportedTypeFromPlatformReference()
    {
        const string code = "Reg";

        var items = await service.GetCompletionsAsync(SessionContext.Empty, code, code.Length);

        var candidate = Assert.Single(items, static item =>
            item.TextToInsert == "Regex" && item.RequiredNamespace == "System.Text.RegularExpressions");
        Assert.Equal("using System.Text.RegularExpressions", candidate.NamespaceDisplayText);
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
                "/// <summary>Describes a local state.</summary>\nenum LocalState\n{\n    /// <summary>The initial state.</summary>\n    Ready = 4,\n    Busy = 8\n}",
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
        var sessionEnumMember = Assert.Single(entries, static entry =>
            entry.Namespace == "(session)" && entry.ContainingType == "LocalState" &&
            entry.Name == "Ready" && entry.Kind == "enum member");
        Assert.Equal("Ready = 4", sessionEnumMember.DisplayName);
        Assert.Contains("initial", sessionEnumMember.Summary, StringComparison.OrdinalIgnoreCase);
        var inheritedClass = Assert.Single(entries, static entry =>
            entry.Namespace == "(session)" && entry.Name == "Customer" && entry.Kind == "class");
        Assert.Equal(["EntityBase", "IEntity"], inheritedClass.InheritedTypes);
        Assert.Contains(entries, static entry =>
            entry.Namespace == "PlayGroundSharp.TestFixture" && entry.Name == "Greeter");
        var dynamicEnumMember = Assert.Single(entries, static entry =>
            entry.Namespace == "PlayGroundSharp.TestFixture" && entry.ContainingType == "WorkflowState" &&
            entry.Name == "Running" && entry.Kind == "enum member");
        Assert.Equal("Running = 20", dynamicEnumMember.DisplayName);
        Assert.Contains("currently running", dynamicEnumMember.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(entries, static entry =>
            entry.Namespace == "System" && entry.ContainingType == "DayOfWeek" &&
            entry.Name == "Sunday" && entry.DisplayName == "Sunday = 0" && entry.Kind == "enum member");

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
