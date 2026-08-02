using PlayGroundSharp.Core;
using PlayGroundSharp.LanguageService;

namespace PlayGroundSharp.App.Tests;

public sealed class ResultExpressionBuilderTests
{
    [Fact]
    public void CellAndFlattenOperationsFollowTheOriginalResult()
    {
        var customers = Sequence(
            Row(("Name", Text("Ada")), ("Orders", Sequence(Row(("Id", Number("101")))))),
            Row(("Name", Text("Grace")), ("Orders", Sequence(Row(("Id", Number("201")))))));
        var table = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(customers));
        var ordersColumn = table.Columns.ToList().IndexOf("Orders");

        var cell = ResultExpressionBuilder.ForCell("customers", table, table.Rows[1], ordersColumn);
        var flattened = ResultExpressionBuilder.ForFlattenedColumn("customers", table, ordersColumn);

        Assert.Equal("customers.ElementAt(1).Orders", cell);
        Assert.Equal("customers.SelectMany(item => item.Orders)", flattened);
    }

    [Fact]
    public async Task JsonArraysUseJsonNodeAccessors()
    {
        var jsonArray = new ResultSnapshot(
            SnapshotKind.Json,
            "1 item",
            "System.Text.Json.Nodes.JsonArray",
            Items:
            [
                new(
                    SnapshotKind.Json,
                    "1 property",
                    "System.Text.Json.Nodes.JsonObject",
                    Properties:
                    [
                        new("order-items", new(
                            SnapshotKind.Json,
                            "1 item",
                            "System.Text.Json.Nodes.JsonArray",
                            Items: [Number("101")]))
                    ])
            ]);
        var table = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(jsonArray));

        var flattened = ResultExpressionBuilder.ForFlattenedColumn("json", table, 0);

        Assert.Equal("json!.AsArray().SelectMany(item => item![\"order-items\"]!.AsArray())", flattened);
        var diagnostics = await new CSharpLanguageService().GetDiagnosticsAsync(
            SessionContext.Empty with
            {
                Submissions =
                [
                    "JsonArray json = JsonNode.Parse(\"[{\\\"order-items\\\":[1]}]\")!.AsArray();"
                ]
            },
            flattened!);
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public async Task MixedColumnUsesShapeSafeFlatteningExpression()
    {
        var customers = Sequence(
            Row(("Orders", Sequence(Row(("Id", Number("101")))))),
            Row(("Orders", Row(("Id", Number("201"))))));
        var table = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(customers));

        var expression = ResultExpressionBuilder.ForFlattenedColumn("customers", table, 0);
        Assert.Equal(
            "customers.SelectMany(item => PlayGroundSharp.Core.ResultQuery.Flatten(" +
            "PlayGroundSharp.Core.ResultQuery.Property(item, \"Orders\")))",
            expression);
        var diagnostics = await new CSharpLanguageService().GetDiagnosticsAsync(
            SessionContext.Empty with
            {
                Submissions =
                [
                    "var customers = new[] { " +
                    "new { Orders = (object?)new[] { new { Id = 1 } } }, " +
                    "new { Orders = (object?)new { Id = 2 } }, " +
                    "new { Orders = (object?)null } };"
                ]
            },
            expression!);
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void MissingCellsUseShapeSafeFlatteningExpression()
    {
        var customers = Sequence(
            Row(("Orders", Sequence(Row(("Id", Number("101")))))),
            Row(("Name", Text("Grace"))));
        var table = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(customers));
        var ordersColumn = table.Columns.ToList().IndexOf("Orders");

        Assert.Equal(
            "customers.SelectMany(item => PlayGroundSharp.Core.ResultQuery.Flatten(" +
            "PlayGroundSharp.Core.ResultQuery.Property(item, \"Orders\")))",
            ResultExpressionBuilder.ForFlattenedColumn("customers", table, ordersColumn));
    }

    [Fact]
    public void StringDictionaryUsesAnIndexer()
    {
        var dictionary = new ResultSnapshot(
            SnapshotKind.Object,
            "1 entry",
            "System.Collections.Generic.Dictionary`2[System.String,System.Object]",
            Properties: [new("order-items", Sequence(Number("1")))]);
        var table = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(dictionary));

        var expression = ResultExpressionBuilder.ForCell("values", table, table.Rows[0], 0);

        Assert.Equal("values[\"order-items\"]", expression);
    }

    [Fact]
    public async Task ResultHistoryFallbackRemainsUsableForNavigationAndFlattening()
    {
        var customers = Sequence(
            Row(("Orders", Sequence(Row(("Id", Number("101")))))),
            Row(("Orders", Sequence(Row(("Id", Number("201")))))));
        var table = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(customers));

        var cellExpression = ResultExpressionBuilder.ForCell("Out[1]", table, table.Rows[0], 0);
        var flattenedExpression = ResultExpressionBuilder.ForFlattenedColumn("Out[1]", table, 0);

        Assert.Contains("ResultQuery.Property", cellExpression, StringComparison.Ordinal);
        Assert.Contains("ResultQuery.Flatten", flattenedExpression, StringComparison.Ordinal);
        Assert.Contains("SelectMany", flattenedExpression, StringComparison.Ordinal);
        var service = new CSharpLanguageService();
        Assert.DoesNotContain(
            await service.GetDiagnosticsAsync(SessionContext.Empty, cellExpression!),
            static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.DoesNotContain(
            await service.GetDiagnosticsAsync(SessionContext.Empty, flattenedExpression!),
            static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    private static ResultSnapshot Row(params (string Name, ResultSnapshot Value)[] properties) => new(
        SnapshotKind.Object,
        $"{properties.Length} properties",
        "Customer",
        Properties: properties.Select(static property => new ResultProperty(property.Name, property.Value)).ToArray());

    private static ResultSnapshot Sequence(params ResultSnapshot[] items) => new(
        SnapshotKind.Sequence,
        $"{items.Length} items",
        "System.Collections.Generic.List`1",
        Items: items,
        TotalCount: items.Length);

    private static ResultSnapshot Number(string value) => new(SnapshotKind.Number, value, "System.Int32");
    private static ResultSnapshot Text(string value) => new(SnapshotKind.String, value, "System.String");
}
