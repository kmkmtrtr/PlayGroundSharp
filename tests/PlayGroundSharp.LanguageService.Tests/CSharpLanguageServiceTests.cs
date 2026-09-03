using PlayGroundSharp.Core;
using PlayGroundSharp.LanguageService;
using PlayGroundSharp.TestFixture;

namespace PlayGroundSharp.LanguageService.Tests;

public sealed class CSharpLanguageServiceTests
{
    private readonly CSharpLanguageService service = new();

    [Fact]
    public async Task SelectedFrameworkReferencePackControlsAvailableApis()
    {
        var framework = DotNetFrameworkLocator.Discover()
            .FirstOrDefault(static candidate => candidate.Version.Major == 9);
        if (framework is null) return;
        const string code =
            "new[] { 1 }.LeftJoin(new[] { 1 }, x => x, x => x, (left, right) => left)";
        var context = SessionContext.Empty with
        {
            FrameworkReferencePaths = framework.GetReferencePaths()
        };

        var diagnostics = await service.GetDiagnosticsAsync(context, code);

        Assert.Contains(diagnostics, static diagnostic =>
            diagnostic.Level == DiagnosticLevel.Error &&
            diagnostic.Message.Contains("LeftJoin", StringComparison.Ordinal));
    }

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
    public void CompactsLongSignatureListsWithoutShorteningTheMethodName()
    {
        var signature = new SignatureInformation(
            "(int Left, int Right) ExtremelyLongMethodNameThatMustRemainVisible" +
            "(int first, string second, bool third, decimal fourth, object fifth)",
            string.Empty,
            [
                new("first", "int", string.Empty),
                new("second", "string", string.Empty),
                new("third", "bool", string.Empty),
                new("fourth", "decimal", string.Empty),
                new("fifth", "object", string.Empty)
            ],
            0);

        Assert.Equal(
            "(int Left, int Right) ExtremelyLongMethodNameThatMustRemainVisible" +
            "(int first, string second, … +3)",
            signature.ListDisplayText);
        Assert.Equal(signature.DisplayText, signature.AccessibleDisplayText);
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
    public async Task CompletesMembersAfterATypedRetainedResultIsNamed()
    {
        var context = new SessionContext(
            ["var json = RetainResultAs<global::System.Text.Json.Nodes.JsonObject>(3);"],
            SessionContext.DefaultImports,
            []);
        const string code = "json.";

        var items = await service.GetCompletionsAsync(context, code, code.Length);
        var diagnostics = await service.GetDiagnosticsAsync(context, "json.ContainsKey(\"name\")");
        var names = items.Select(static item => item.DisplayText).ToHashSet(StringComparer.Ordinal);

        Assert.True(names.Contains("TryGetPropertyValue"),
            string.Join(" | ", diagnostics.Select(static item => $"{item.Id}: {item.Message}")));
        Assert.Contains("Add", names);
        Assert.Contains("ToJsonString", names);
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public async Task UsesRecoveredVariableTypeForDynamicHistoryAndLatestRedeclaration()
    {
        var context = new SessionContext(
            ["var value = 42;", "var value = Out[1];"],
            SessionContext.DefaultImports,
            [],
            VariableTypeHints:
            [
                new("value", "string")
            ]);

        var items = await service.GetCompletionsAsync(context, "value.", "value.".Length);

        Assert.Contains(items, static item => item.DisplayText == "Length");
        Assert.Contains(items, static item => item.DisplayText == "Contains");
        Assert.Contains(items, static item => item.DisplayText == "Substring");
    }

    [Fact]
    public async Task UsesRecoveredTupleNamesForCompletion()
    {
        var context = new SessionContext(
            ["var pair = Out[1];"],
            SessionContext.DefaultImports,
            [],
            VariableTypeHints:
            [
                new("pair", "(int index, int delay)")
            ]);

        var items = await service.GetCompletionsAsync(context, "pair.", "pair.".Length);

        Assert.Contains(items, static item => item.DisplayText == "index");
        Assert.Contains(items, static item => item.DisplayText == "delay");
        Assert.DoesNotContain(items, static item => item.DisplayText == "Item1");
        Assert.DoesNotContain(items, static item => item.DisplayText == "Item2");

        var ctrlSpaceItems = await service.GetCompletionsAsync(context, "pair", "pair".Length);
        var delay = Assert.Single(ctrlSpaceItems, static item => item.DisplayText == "delay");
        Assert.Equal(".delay", delay.TextToInsert);
        Assert.Equal("pair".Length, delay.ReplacementStart);
    }

    [Fact]
    public async Task PreservesOutVariablesDeclaredByHistoricalResultExpressions()
    {
        var context = new SessionContext(
            ["int.TryParse(\"42\", out var parsed)"],
            SessionContext.DefaultImports,
            []);

        var items = await service.GetCompletionsAsync(context, "parsed.", "parsed.".Length);

        Assert.Contains(items, static item => item.DisplayText == "CompareTo");
        Assert.Contains(items, static item => item.DisplayText == "ToString");
    }

    [Fact]
    public async Task CtrlSpaceAfterSessionVariableCompletesItsMembersAndInsertsTheDot()
    {
        var context = new SessionContext(
            ["var values = Enumerable.Range(1, 10).ToArray();"],
            SessionContext.DefaultImports,
            []);

        var items = await service.GetCompletionsAsync(context, "values", "values".Length);

        var length = Assert.Single(items, static item => item.DisplayText == "Length");
        var description = await service.GetCompletionDescriptionAsync(
            context, "values", "values".Length, length);
        Assert.Equal(".Length", length.TextToInsert);
        Assert.Equal("values".Length, length.ReplacementStart);
        Assert.NotNull(description);
        Assert.Contains(items, static item => item.DisplayText == "Where" && item.TextToInsert == ".Where");
    }

    [Fact]
    public async Task CtrlSpaceAfterPartialVariableNameKeepsIdentifierCompletion()
    {
        var context = new SessionContext(
            ["var typedResult = Enumerable.Range(1, 10).ToArray();"],
            SessionContext.DefaultImports,
            []);

        var items = await service.GetCompletionsAsync(context, "typedRes", "typedRes".Length);

        var variable = Assert.Single(items, static item => item.DisplayText == "typedResult");
        Assert.Equal("typedResult", variable.TextToInsert);
        Assert.Null(variable.ReplacementStart);
    }

    [Fact]
    public async Task CtrlSpaceAfterIndexedAndInvokedExpressionsCompletesTheirMembers()
    {
        var context = new SessionContext(
            ["var typedResult = Enumerable.Range(1, 10).ToArray();"],
            SessionContext.DefaultImports,
            []);

        var indexed = await service.GetCompletionsAsync(
            context, "typedResult[0]", "typedResult[0]".Length);
        var invoked = await service.GetCompletionsAsync(
            context, "typedResult.First()", "typedResult.First()".Length);

        Assert.Contains(indexed, static item => item.DisplayText == "CompareTo" && item.TextToInsert == ".CompareTo");
        Assert.Contains(invoked, static item => item.DisplayText == "CompareTo" && item.TextToInsert == ".CompareTo");
    }

    [Fact]
    public async Task CompletesLargeDataHelpers()
    {
        const string code = "Data.";

        var items = await service.GetCompletionsAsync(SessionContext.Empty, code, code.Length);

        Assert.Contains(items, static item => item.DisplayText == "PreviewText");
        Assert.Contains(items, static item => item.DisplayText == "ReadLines");
        Assert.Contains(items, static item => item.DisplayText == "ReadAllTextAsync");
        Assert.Contains(items, static item => item.DisplayText == "ReadAllBytesAsync");
        Assert.Contains(items, static item => item.DisplayText == "ReadJsonAsync");
        Assert.Contains(items, static item => item.DisplayText == "ReadJsonArrayAsync");
        Assert.Contains(items, static item => item.DisplayText == "ReadJsonLinesAsync");
        Assert.Contains(items, static item => item.DisplayText == "ReadAllJsonLinesAsync");
        Assert.Contains(items, static item => item.DisplayText == "StreamJsonLinesAsync");
        Assert.Contains(items, static item => item.DisplayText == "ReadCsvAsync");
        Assert.Contains(items, static item => item.DisplayText == "ReadTsvAsync");
        Assert.Contains(items, static item => item.DisplayText == "ReadDelimitedAsync");
        Assert.Contains(items, static item => item.DisplayText == "StreamDelimitedAsync");
    }

    [Fact]
    public async Task CompletesHttpClientMembersWhenSystemNetHttpIsImported()
    {
        var context = SessionContext.Empty with
        {
            Imports = [.. SessionContext.DefaultImports, "System.Net.Http"]
        };
        const string code = "var client = new HttpClient(); client.";

        var items = await service.GetCompletionsAsync(context, code, code.Length);
        var diagnostics = await service.GetDiagnosticsAsync(
            context,
            "var client = new HttpClient(); client.GetAsync(\"https://example.com\")");

        Assert.Contains(items, static item => item.DisplayText == "GetAsync");
        Assert.Contains(items, static item => item.DisplayText == "SendAsync");
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public async Task CompletesJsonNodeMembersAfterReadingJsonWithExecutionCancellation()
    {
        var context = new SessionContext(
            ["""var json = await Data.ReadJsonAsync(@"C:\data\sample.json", ExecutionCancellation)"""],
            SessionContext.DefaultImports,
            []);
        const string code = "json.";

        var items = await service.GetCompletionsAsync(context, code, code.Length);
        var diagnostics = await service.GetDiagnosticsAsync(context, code);
        var names = items.Select(static item => item.DisplayText).ToHashSet(StringComparer.Ordinal);

        Assert.True(names.Contains("AsObject"),
            string.Join(" | ", diagnostics.Select(static item => $"{item.Id}: {item.Message}")));
        Assert.Contains("AsArray", names);
        Assert.Contains("GetValueKind", names);
        Assert.Contains("ToJsonString", names);
    }

    [Fact]
    public async Task CompletesJsonNodeMembersWithinTheReadingSubmission()
    {
        const string code = """
            var json = await Data.ReadJsonAsync(@"C:\data\sample.json", ExecutionCancellation);
            json.
            """;

        var items = await service.GetCompletionsAsync(SessionContext.Empty, code, code.Length);

        Assert.Contains(items, static item => item.DisplayText == "AsObject");
        Assert.Contains(items, static item => item.DisplayText == "AsArray");
        Assert.Contains(items, static item => item.DisplayText == "GetValue");
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

        Assert.DoesNotContain(items, static item => item.DisplayText == "BoundedJsonNodeList");
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
    public async Task CompletesNumericLiteralImmediatelyAfterTheDot()
    {
        var items = await service.GetCompletionsAsync(SessionContext.Empty, "1.", "1.".Length);

        var toString = Assert.Single(items, static item => item.DisplayText == "ToString");
        Assert.Equal(0, toString.ReplacementStart);
        Assert.Equal("(1).ToString", toString.TextToInsert);
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

    [Fact]
    public async Task ReturnsConstructorSignatureHelp()
    {
        var context = new SessionContext(
            ["record User(string Name, int Age)"],
            SessionContext.DefaultImports,
            []);
        const string code = "new User(";

        var help = await service.GetSignatureHelpAsync(context, code, code.Length);

        Assert.NotNull(help);
        var signature = Assert.Single(help.Signatures);
        Assert.Contains("string Name", signature.DisplayText, StringComparison.Ordinal);
        Assert.Contains("int Age", signature.DisplayText, StringComparison.Ordinal);
        Assert.Equal("Name", signature.Parameters[signature.ActiveParameter].Name);
    }

    [Fact]
    public async Task SelectsConstructorOverloadCompatibleWithEnteredArguments()
    {
        var context = new SessionContext(
            [
                "class Widget { public Widget(string name) { } public Widget(int count, string name) { } }"
            ],
            SessionContext.DefaultImports,
            []);
        const string code = "new Widget(42, ";

        var help = await service.GetSignatureHelpAsync(context, code, code.Length);

        Assert.NotNull(help);
        Assert.Equal(2, help.Signatures.Count);
        var selected = help.Signatures[help.SelectedSignature];
        Assert.Contains("int count", selected.DisplayText, StringComparison.Ordinal);
        Assert.Contains("string name", selected.DisplayText, StringComparison.Ordinal);
        Assert.Equal("name", selected.Parameters[selected.ActiveParameter].Name);
    }

    [Fact]
    public async Task CompletesExtensionsFromASeparateDynamicReferenceWhenRegularMembersExist()
    {
        var context = new SessionContext(
            ["var connection = new PlayGroundSharp.TestDependency.FixtureConnection();"],
            SessionContext.DefaultImports,
            [typeof(PlayGroundSharp.TestDependency.FixtureConnection).Assembly.Location, typeof(Greeter).Assembly.Location]);
        const string code = "connection.Que";

        var items = await service.GetCompletionsAsync(context, code, code.Length);

        Assert.Contains(items, static item => item.DisplayText == "ConnectionString");
        var query = Assert.Single(items, static item => item.DisplayText == "Query");
        Assert.True(query.IsExtensionMethod);
        Assert.Equal("PlayGroundSharp.TestFixture", query.RequiredNamespace);
    }

    [Theory]
    [InlineData("1 + 2")]
    [InlineData("delayed + 1")]
    [InlineData("delayed")]
    [InlineData("delayed;")]
    [InlineData("delayed:")]
    [InlineData("var delayed = 43;")]
    [InlineData("var next = delayed + 1")]
    public async Task DoesNotReportErrorsForValidTrailingExpressions(string currentCode)
    {
        var context = new SessionContext(["var delayed = 42;"], SessionContext.DefaultImports, []);

        var diagnostics = await service.GetDiagnosticsAsync(context, currentCode);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task KeepsUndefinedNameErrorsInTrailingExpressions()
    {
        var diagnostics = await service.GetDiagnosticsAsync(SessionContext.Empty, "missingValue;");

        Assert.Contains(diagnostics, static diagnostic =>
            diagnostic.Level == DiagnosticLevel.Error && diagnostic.Id == "CS0103");
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id == "CS0201");
    }

    [Fact]
    public async Task ReconstructsPriorTrailingValuesWithExecutionSemantics()
    {
        var context = new SessionContext(
            ["var delayed = 42;", "delayed;"],
            SessionContext.DefaultImports,
            []);

        var diagnostics = await service.GetDiagnosticsAsync(context, "delayed");

        Assert.Empty(diagnostics);
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
    public async Task DoesNotDuplicateRoslynAndXmlCompletionDocumentation()
    {
        const string code = "Enumerable.Em";
        var items = await service.GetCompletionsAsync(SessionContext.Empty, code, code.Length);
        var candidate = Assert.Single(items, static item => item.DisplayText == "Empty");

        var description = await service.GetCompletionDescriptionAsync(
            SessionContext.Empty, code, code.Length, candidate);

        Assert.NotNull(description);
        Assert.Equal(
            1,
            description.Split("Returns an empty", StringSplitOptions.None).Length - 1);
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
        Assert.True(quickInfo.ContainsPosition(code.IndexOf("Length", StringComparison.Ordinal) + 2));
        Assert.Equal("system.string.length", quickInfo.DocumentationPath);
    }

    [Fact]
    public async Task ReturnsMethodAndParameterDocumentationForInvocationHover()
    {
        var context = new SessionContext(
            [],
            SessionContext.DefaultImports,
            [typeof(Greeter).Assembly.Location]);
        const string code = "PlayGroundSharp.TestFixture.Greeter.Greet(\"Ada\")";

        var methodInfo = await service.GetQuickInfoAsync(
            context,
            code,
            code.IndexOf("Greet", StringComparison.Ordinal) + 2);
        var argumentInfo = await service.GetQuickInfoAsync(
            context,
            code,
            code.IndexOf("Ada", StringComparison.Ordinal) + 1);

        Assert.NotNull(methodInfo);
        Assert.Contains("Creates a greeting", methodInfo.Text, StringComparison.Ordinal);
        Assert.Contains("The person to greet", methodInfo.Text, StringComparison.Ordinal);
        Assert.NotNull(argumentInfo);
        Assert.Contains("name : string", argumentInfo.Text, StringComparison.Ordinal);
        Assert.Contains("The person to greet", argumentInfo.Text, StringComparison.Ordinal);
        Assert.Null(methodInfo.DocumentationPath);
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
                """
                class PropertySample
                {
                    /// <summary>Gets or initializes the display name.</summary>
                    public string Name { get; init; } = string.Empty;

                    public int Count { get; private set; }

                    /// <summary>Gets the value at an index.</summary>
                    /// <param name="index">The requested index.</param>
                    public string this[int index] => index.ToString();
                }
                """,
                "/// <summary>Checks whether a user is an adult.</summary>\n/// <param name=\"user\">The user to inspect.</param>\nbool IsAdult(User user) => true"
            ],
            SessionContext.DefaultImports,
            [typeof(Greeter).Assembly.Location]);

        var entries = await service.GetSymbolExplorerAsync(context);

        Assert.Contains(entries, static entry => entry.Namespace == "System" && entry.Name == "String" && entry.Kind == "class");
        Assert.Equal(
            "system.collections.generic.list-1",
            Assert.Single(entries, static entry =>
                entry.Namespace == "System.Collections.Generic" && entry.Name == "List<T>" &&
                entry.Kind == "class").DocumentationPath);
        var frameworkProperty = Assert.Single(entries, static entry =>
            entry.Namespace == "System" && entry.ContainingType == "String" &&
            entry.Name == "Length" && entry.Kind == "property");
        Assert.Equal("Length : int", frameworkProperty.DisplayName);
        Assert.Equal("int String.Length { get; }", frameworkProperty.Signature);
        Assert.Equal("system.string.length", frameworkProperty.DocumentationPath);
        Assert.Contains(entries, static entry => entry.Namespace == "System.Linq" && entry.Name == "Enumerable" && entry.Kind == "class");
        Assert.Contains(entries, static entry => entry.Namespace == "(session)" && entry.Name == "User" && entry.Kind == "record");
        Assert.Contains(entries, static entry =>
            entry.Namespace == "(session)" && entry.ContainingType == "User" &&
            entry.Name == "Name" && entry.Kind == "property");
        Assert.Contains(entries, static entry => entry.Namespace == "(session)" && entry.Name == "Transformer" && entry.Kind == "delegate");
        var sessionEnumMember = Assert.Single(entries, static entry =>
            entry.Namespace == "(session)" && entry.ContainingType == "LocalState" &&
            entry.Name == "Ready" && entry.Kind == "enum member");
        Assert.Equal("Ready = 4", sessionEnumMember.DisplayName);
        Assert.Contains("initial", sessionEnumMember.Summary, StringComparison.OrdinalIgnoreCase);
        var inheritedClass = Assert.Single(entries, static entry =>
            entry.Namespace == "(session)" && entry.Name == "Customer" && entry.Kind == "class");
        Assert.Equal(["EntityBase", "IEntity"], inheritedClass.InheritedTypes);
        Assert.NotNull(inheritedClass.SymbolId);
        var entityBase = Assert.Single(entries, static entry =>
            entry.Namespace == "(session)" && entry.Name == "EntityBase" && entry.Kind == "class");
        var entityInterface = Assert.Single(entries, static entry =>
            entry.Namespace == "(session)" && entry.Name == "IEntity" && entry.Kind == "interface");
        Assert.Collection(
            inheritedClass.InheritedTypeRelations,
            relation =>
            {
                Assert.Equal(entityBase.SymbolId, relation.SymbolId);
                Assert.Equal("class", relation.Kind);
            },
            relation =>
            {
                Assert.Equal(entityInterface.SymbolId, relation.SymbolId);
                Assert.Equal("interface", relation.Kind);
            });
        Assert.Contains(entries, static entry =>
            entry.Namespace == "PlayGroundSharp.TestFixture" && entry.Name == "Greeter");
        var dynamicProperty = Assert.Single(entries, static entry =>
            entry.Namespace == "PlayGroundSharp.TestFixture" && entry.ContainingType == "Greeter" &&
            entry.Name == "Message" && entry.Kind == "property");
        Assert.Equal("static string Greeter.Message { get; }", dynamicProperty.Signature);
        Assert.Contains("greeting", dynamicProperty.Summary, StringComparison.OrdinalIgnoreCase);
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

        var initProperty = Assert.Single(entries, static entry =>
            entry.Namespace == "(session)" && entry.ContainingType == "PropertySample" &&
            entry.Name == "Name" && entry.Kind == "property");
        Assert.Equal("string PropertySample.Name { get; init; }", initProperty.Signature);
        Assert.Contains("display name", initProperty.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "int PropertySample.Count { get; private set; }",
            Assert.Single(entries, static entry =>
                entry.Namespace == "(session)" && entry.ContainingType == "PropertySample" &&
                entry.Name == "Count" && entry.Kind == "property").Signature);
        var indexer = Assert.Single(entries, static entry =>
            entry.Namespace == "(session)" && entry.ContainingType == "PropertySample" &&
            entry.Name == "this" && entry.Kind == "property");
        Assert.Equal("this[int index] : string", indexer.DisplayName);
        Assert.Equal("string PropertySample.this[int index] { get; }", indexer.Signature);
        Assert.Equal("The requested index.", Assert.Single(indexer.Parameters).Summary);

        var frameworkMethod = Assert.Single(entries, static entry =>
            entry.Namespace == "System" && entry.ContainingType == "String" &&
            entry.Name == "Contains" && entry.Parameters.Count == 1 && entry.Parameters[0].TypeName == "string");
        Assert.NotEmpty(frameworkMethod.Summary);
        Assert.NotEmpty(frameworkMethod.Parameters[0].Summary);
        Assert.Equal("system.string.contains", frameworkMethod.DocumentationPath);
        Assert.Equal(
            "system.dayofweek",
            Assert.Single(entries, static entry =>
                entry.Namespace == "System" && entry.ContainingType == "DayOfWeek" &&
                entry.Name == "Sunday" && entry.Kind == "enum member").DocumentationPath);

        var dynamicMethod = Assert.Single(entries, static entry =>
            entry.Namespace == "PlayGroundSharp.TestFixture" && entry.ContainingType == "Greeter" && entry.Name == "Greet");
        Assert.Equal("Creates a greeting for the specified person.", dynamicMethod.Summary);
        Assert.Equal("The person to greet.", Assert.Single(dynamicMethod.Parameters).Summary);
    }
}
