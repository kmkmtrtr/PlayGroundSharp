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
        Assert.Equal("ordersTyped", DataTypeInference.SuggestVariableName("orders"));
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
