using PlayGroundSharp.Core;
using PlayGroundSharp.LanguageService;
using PlayGroundSharp.Worker;

namespace PlayGroundSharp.App.Tests;

public sealed class DataTypeInferenceTests
{
    [Fact]
    public void InfersNestedRowsMissingValuesAndJsonPropertyNames()
    {
        var first = JsonObject(
            ("order-id", Number("1")),
            ("price", Number("12.5")),
            ("discount", Null()),
            ("shipping-address", JsonObject(("city", String("Tokyo")))));
        var second = JsonObject(
            ("order-id", Number("2")),
            ("price", Number("4")),
            ("discount", Number("1.0")));
        var snapshot = JsonArray(first, second);

        var result = DataTypeInference.Generate(snapshot, "orders", "OrderItem", "ordersTyped");

        Assert.NotNull(result);
        Assert.Equal("List<OrderItem>", result.TargetType);
        Assert.Contains("public sealed class OrderItem", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public sealed class ShippingAddress", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("JsonPropertyName(\"order-id\")]", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public decimal Price", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public decimal? Discount", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public ShippingAddress? ShippingAddress", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("JsonSerializer.Serialize(orders)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FallsBackForMixedPropertyShapes()
    {
        var snapshot = JsonArray(
            JsonObject(("value", String("text"))),
            JsonObject(("value", JsonObject(("nested", Number("1"))))));

        var result = DataTypeInference.Generate(snapshot, "rows", "Row", "rowsTyped");

        Assert.NotNull(result);
        Assert.Contains("JsonNode Value", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(DataTypeInferenceWarning.FallbackType, result.Warnings);
    }

    [Fact]
    public async Task AvoidsPropertyNamesMatchingTheirContainingType()
    {
        var snapshot = JsonObject(("a", JsonObject(("a", Number("1")))));

        var result = DataTypeInference.Generate(snapshot, "json", "RootModel", "typedJson")!;
        var service = new CSharpLanguageService();
        var diagnostics = await service.GetDiagnosticsAsync(
            SessionContext.Empty with
            {
                Submissions = ["JsonNode json = JsonNode.Parse(\"{\\\"a\\\":{\\\"a\\\":1}}\")!;"]
            },
            result.GeneratedCode);

        Assert.Contains("public sealed class A", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("JsonPropertyName(\"a\")", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public int A2", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void InfersClrQueryRowsUsingRuntimeNumberTypes()
    {
        var row = new ResultSnapshot(
            SnapshotKind.Object,
            "{ Customer = A, Price = 12.5, Quantity = 2 }",
            "QueryRow",
            [
                new("Customer", new(SnapshotKind.String, "A", "System.String")),
                new("Price", new(SnapshotKind.Number, "12.5", "System.Decimal")),
                new("Quantity", new(SnapshotKind.Number, "2", "System.Int32"))
            ]);
        var rows = new ResultSnapshot(
            SnapshotKind.Sequence,
            "1 item",
            "QueryRow[]",
            Items: [row],
            TotalCount: 1);

        var result = DataTypeInference.Generate(rows, "queryRows", "QueryRowModel", "queryRowsTyped");

        Assert.NotNull(result);
        Assert.Contains("public decimal Price", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public int Quantity", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsEmptyAndScalarRoots()
    {
        Assert.False(DataTypeInference.CanInfer(JsonArray()));
        Assert.Null(DataTypeInference.Generate(JsonArray(), "rows", "Row", "rowsTyped"));
        Assert.False(DataTypeInference.CanInfer(String("value")));
    }

    [Fact]
    public void SuggestsReadableNamesForSelectedVariables()
    {
        var rows = JsonArray(JsonObject(("value", Number("1"))));

        Assert.Equal("OrderItem", DataTypeInference.SuggestTypeName("orders", rows));
        Assert.Equal("typedOrders", DataTypeInference.SuggestVariableName("orders"));
        Assert.Equal("SettingsModel", DataTypeInference.SuggestTypeName("settings", JsonObject()));
    }

    [Fact]
    public async Task GeneratedSubmissionProvidesTypedCompletion()
    {
        var snapshot = JsonArray(JsonObject(
            ("customer", String("A")),
            ("quantity", Number("2"))));
        var result = DataTypeInference.Generate(snapshot, "orders", "OrderItem", "ordersTyped")!;
        var service = new CSharpLanguageService();
        var source = "JsonNode orders = JsonNode.Parse(\"[{\\\"customer\\\":\\\"A\\\",\\\"quantity\\\":2}]\")!;";

        var diagnostics = await service.GetDiagnosticsAsync(
            SessionContext.Empty with { Submissions = [source] },
            result.GeneratedCode);
        var completions = await service.GetCompletionsAsync(
            SessionContext.Empty with { Submissions = [source, result.GeneratedCode] },
            "ordersTyped[0].",
            "ordersTyped[0].".Length);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.Contains(completions, candidate => candidate.DisplayText == "Customer");
        Assert.Contains(completions, candidate => candidate.DisplayText == "Quantity");
    }

    [Fact]
    public async Task GeneratedSubmissionCreatesTypedVariableInWorker()
    {
        var snapshot = JsonArray(JsonObject(
            ("customer", String("A")),
            ("quantity", Number("2"))));
        var generated = DataTypeInference.Generate(snapshot, "orders", "OrderItem", "ordersTyped")!;
        var session = new ScriptSession();
        var source = await session.ExecuteAsync(
            1,
            "JsonNode orders = JsonNode.Parse(\"[{\\\"customer\\\":\\\"A\\\",\\\"quantity\\\":2}]\")!;");

        var result = await session.ExecuteAsync(2, generated.GeneratedCode);

        Assert.True(source.StateAccepted);
        Assert.True(result.StateAccepted);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        var variable = Assert.Single(session.GetVariables(), variable => variable.Name == "ordersTyped");
        var row = Assert.Single(variable.Value.Items!);
        Assert.Contains(row.Properties!, property => property.Name == "Customer" && property.Value.Display == "A");
        Assert.Contains(row.Properties!, property => property.Name == "Quantity" && property.Value.Display == "2");
    }

    [Fact]
    public async Task ClrProjectionPreservesIPAddressWithoutJsonRoundTrip()
    {
        var session = new ScriptSession();
        var source = await session.ExecuteAsync(
            1,
            "dynamic row = new global::System.Dynamic.ExpandoObject(); " +
            "row.Host = \"local\"; row.Address = global::System.Net.IPAddress.Loopback; " +
            "var rows = new[] { row }; rows");
        var generated = DataTypeInference.Generate(source.Snapshot!, "rows", "Endpoint", "typedRows")!;

        Assert.Contains("global::System.Net.IPAddress Address", generated.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializer", generated.GeneratedCode, StringComparison.Ordinal);

        var projection = await session.ExecuteAsync(2, generated.GeneratedCode);
        var verification = await session.ExecuteAsync(3, "typedRows[0].Address.ToString()");

        Assert.True(source.StateAccepted);
        Assert.True(projection.StateAccepted);
        Assert.DoesNotContain(projection.Diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.True(verification.StateAccepted);
        Assert.Equal("127.0.0.1", verification.Snapshot?.Display);
    }

    [Fact]
    public async Task ClrProjectionConvertsJsonValuesToInferredScalarTypes()
    {
        var session = new ScriptSession();
        var source = await session.ExecuteAsync(
            1,
            "var rows = new[] { JsonNode.Parse(\"{\\\"name\\\":\\\"Ada\\\",\\\"age\\\":42}\")! }; rows");
        var generated = DataTypeInference.Generate(source.Snapshot!, "rows", "Person", "typedRows")!;

        var projection = await session.ExecuteAsync(2, generated.GeneratedCode);
        var verification = await session.ExecuteAsync(3, "typedRows[0].Name + \"!\" + typedRows[0].Age");

        Assert.True(source.StateAccepted);
        Assert.True(projection.StateAccepted);
        Assert.DoesNotContain(projection.Diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.True(verification.StateAccepted);
        Assert.Equal("Ada!42", verification.Snapshot?.Display);
    }

    [Fact]
    public async Task ClrProjectionReadsMembersFromJsonElements()
    {
        var session = new ScriptSession();
        var source = await session.ExecuteAsync(
            1,
            "var rows = new[] { JsonDocument.Parse(\"{\\\"name\\\":\\\"Ada\\\",\\\"age\\\":42}\").RootElement.Clone() }; rows");
        var generated = DataTypeInference.Generate(source.Snapshot!, "rows", "Person", "typedRows")!;

        var projection = await session.ExecuteAsync(2, generated.GeneratedCode);
        var verification = await session.ExecuteAsync(3, "typedRows[0].Name + \"/\" + typedRows[0].Age");

        Assert.True(source.StateAccepted);
        Assert.True(projection.StateAccepted);
        Assert.DoesNotContain(projection.Diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.True(verification.StateAccepted);
        Assert.Equal("Ada/42", verification.Snapshot?.Display);
    }

    [Fact]
    public async Task JsonInferenceMergesDuplicatePropertiesWithoutNameCollisions()
    {
        var session = new ScriptSession();
        var source = await session.ExecuteAsync(
            1,
            "var json = JsonDocument.Parse(\"{\\\"value\\\":\\\"first\\\",\\\"value\\\":2}\").RootElement.Clone(); json");
        var generated = DataTypeInference.Generate(source.Snapshot!, "json", "Model", "typed")!;

        var projection = await session.ExecuteAsync(2, generated.GeneratedCode);
        var verification = await session.ExecuteAsync(3, "typed.Value!.GetValue<int>()");

        Assert.True(source.StateAccepted);
        Assert.Equal(1, generated.GeneratedCode.Split(" Value { get; init; }", StringSplitOptions.None).Length - 1);
        Assert.Contains("JsonNode Value", generated.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(DataTypeInferenceWarning.FallbackType, generated.Warnings);
        Assert.True(projection.StateAccepted);
        Assert.DoesNotContain(projection.Diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.True(verification.StateAccepted);
        Assert.Equal("2", verification.Snapshot?.Display);
    }

    [Fact]
    public async Task ClrProjectionEnumeratesDataTableRows()
    {
        var session = new ScriptSession();
        session.AddReference(typeof(System.Data.DataTable).Assembly.Location);
        var source = await session.ExecuteAsync(
            1,
            "var table = new global::System.Data.DataTable(); " +
            "table.Columns.Add(\"Address\", typeof(global::System.Net.IPAddress)); " +
            "table.Columns.Add(\"Count\", typeof(int)); " +
            "table.Rows.Add(global::System.Net.IPAddress.Parse(\"192.0.2.42\"), 7); table");
        var generated = DataTypeInference.Generate(source.Snapshot!, "table", "Endpoint", "typedRows")!;

        var projection = await session.ExecuteAsync(2, generated.GeneratedCode);
        var verification = await session.ExecuteAsync(
            3,
            "typedRows.Count + \"/\" + typedRows[0].Address + \"/\" + typedRows[0].Count");

        Assert.True(source.StateAccepted);
        Assert.Contains("source is global::System.Data.DataTable table", generated.GeneratedCode, StringComparison.Ordinal);
        Assert.True(projection.StateAccepted);
        Assert.DoesNotContain(projection.Diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.True(verification.StateAccepted);
        Assert.Equal("1/192.0.2.42/7", verification.Snapshot?.Display);
    }

    [Fact]
    public void ClrProjectionOmitsUnreadableProperties()
    {
        var row = new ResultSnapshot(
            SnapshotKind.Object,
            "2 members",
            "Row",
            [
                new("Name", new(SnapshotKind.String, "Ada", "System.String", TypeExpression: "string", IsReferenceType: true)),
                new(
                    "Dangerous",
                    new(SnapshotKind.Exception, "boom", "System.InvalidOperationException"),
                    "string",
                    true,
                    IsReadable: false)
            ]);
        var rows = new ResultSnapshot(SnapshotKind.Sequence, "1 item", "Row[]", Items: [row]);

        var generated = DataTypeInference.Generate(rows, "rows", "RowModel", "typedRows")!;

        Assert.Contains("string Name", generated.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Dangerous", generated.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(DataTypeInferenceWarning.UnreadableProperty, generated.Warnings);
    }

    private static ResultSnapshot JsonObject(params (string Name, ResultSnapshot Value)[] properties) =>
        new(
            SnapshotKind.Json,
            $"{properties.Length} properties",
            "System.Text.Json.Nodes.JsonNode",
            properties.Select(property => new ResultProperty(property.Name, property.Value)).ToArray(),
            TotalCount: properties.Length);

    private static ResultSnapshot JsonArray(params ResultSnapshot[] items) =>
        new(
            SnapshotKind.Json,
            $"{items.Length} items",
            "System.Text.Json.Nodes.JsonNode",
            Items: items,
            TotalCount: items.Length);

    private static ResultSnapshot Number(string value) =>
        new(SnapshotKind.Number, value, "System.Text.Json.Nodes.JsonNode");

    private static ResultSnapshot String(string value) =>
        new(SnapshotKind.String, value, "System.Text.Json.Nodes.JsonNode");

    private static ResultSnapshot Null() =>
        new(SnapshotKind.Null, "null", "System.Text.Json.Nodes.JsonNode");
}
