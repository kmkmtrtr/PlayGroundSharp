using PlayGroundSharp.Core;
using PlayGroundSharp.LanguageService;

namespace PlayGroundSharp.App.Tests;

public sealed class ResultExpressionBuilderTests
{
    [Fact]
    public async Task ColumnLayoutGeneratesAReusableProjection()
    {
        var customers = Sequence(
            Row(("Id", Number("1")), ("Name", Text("Ada")), ("Active", Text("true"))));
        var table = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(customers));

        var expression = ResultExpressionBuilder.ForColumns("customers", table, [1, 0]);

        Assert.Equal(
            "customers.Select(item => new { Name = item.Name, Id = item.Id })",
            expression);
        Assert.DoesNotContain("PlayGroundSharp", expression, StringComparison.Ordinal);
        var diagnostics = await new CSharpLanguageService().GetDiagnosticsAsync(
            SessionContext.Empty with
            {
                Submissions =
                [
                    "var customers = new[] { new { Id = 1, Name = \"Ada\", Active = true } };"
                ]
            },
            expression!);
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public async Task CalculatedColumnUsesAStandardSelectProjection()
    {
        var products = Sequence(
            Row(("Name", Text("Pen")), ("Price", Number("10")), ("Quantity", Number("2"))));
        var table = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(products));

        var expression = ResultExpressionBuilder.ForCalculatedColumn(
            "products",
            table,
            [0, 1, 2],
            "Total",
            "row.Price * row.Quantity",
            2);

        Assert.Equal(
            "products.Select(row => new { Name = row.Name, Price = row.Price, " +
            "Total = row.Price * row.Quantity, Quantity = row.Quantity })",
            expression);
        Assert.DoesNotContain("PlayGroundSharp", expression, StringComparison.Ordinal);
        var diagnostics = await new CSharpLanguageService().GetDiagnosticsAsync(
            SessionContext.Empty with
            {
                Submissions =
                [
                    "var products = new[] { new { Name = \"Pen\", Price = 10, Quantity = 2 } };"
                ]
            },
            expression!);
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public async Task CalculatedColumnCompletionUsesTheSourceRowType()
    {
        var products = Sequence(
            Row(("Name", Text("Pen")), ("Price", Number("10")), ("Quantity", Number("2"))));
        var table = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(products));

        var analysis = Assert.IsType<ResultExpressionBuilder.CompletionAnalysis>(
            ResultExpressionBuilder.ForCalculatedColumnCompletion(
                "products",
                table,
                [0, 1, 2],
                "Total",
                "row.",
                "row.".Length,
                2));

        Assert.Equal('.', analysis.Code[analysis.Position - 1]);
        var completions = await new CSharpLanguageService().GetCompletionsAsync(
            SessionContext.Empty with
            {
                Submissions =
                [
                    "var products = new[] { new { Name = \"Pen\", Price = 10, Quantity = 2 } };"
                ]
            },
            analysis.Code,
            analysis.Position);

        Assert.Contains(completions, static item => item.DisplayText == "Price");
        Assert.Contains(completions, static item => item.DisplayText == "Quantity");
    }

    [Fact]
    public async Task SingleJsonObjectUsesAStandardSingleItemLinqProjection()
    {
        var jsonObject = new ResultSnapshot(
            SnapshotKind.Json,
            "2 properties",
            "System.Text.Json.Nodes.JsonObject",
            Properties: [new("price", Number("10")), new("quantity", Number("2"))]);
        var table = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(jsonObject));

        var expression = ResultExpressionBuilder.ForCalculatedColumn(
            "json![\"product\"]",
            table,
            [0, 1],
            "total",
            "row![\"price\"]!.GetValue<int>() * row![\"quantity\"]!.GetValue<int>()",
            2);

        Assert.Equal(
            "new[] { json![\"product\"] }.Select(row => new { " +
            "price = row![\"price\"], quantity = row![\"quantity\"], " +
            "total = row![\"price\"]!.GetValue<int>() * row![\"quantity\"]!.GetValue<int>() }).Single()",
            expression);
        var diagnostics = await new CSharpLanguageService().GetDiagnosticsAsync(
            SessionContext.Empty with
            {
                Submissions =
                [
                    "JsonNode json = JsonNode.Parse(\"{\\\"product\\\":{\\\"price\\\":10,\\\"quantity\\\":2}}\")!;"
                ]
            },
            expression!);
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void CalculatedColumnRejectsDuplicateOrNonPortableInputs()
    {
        var table = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(
            Sequence(Row(("Id", Number("1")), ("Value", Number("2"))))));

        Assert.Null(ResultExpressionBuilder.ForCalculatedColumn(
            "values", table, [0, 1], "Value", "row.Value * 2", 2));
        Assert.Null(ResultExpressionBuilder.ForCalculatedColumn(
            "values", table, [0, 1], "invalid-name", "row.Value * 2", 2));
        Assert.Null(ResultExpressionBuilder.ForCalculatedColumn(
            "Out[1]", table, [0, 1], "Total", "row.Value * 2", 2));
    }

    [Fact]
    public void ScalarSequenceUsesTheRowAsItsValue()
    {
        var table = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(
            Sequence(Number("1"), Number("2"))));

        Assert.Equal("row", ResultExpressionBuilder.ForCalculatedColumnFormula(table, 0));
        Assert.Equal(
            "new[] { 1, 2 }.Select(row => new { Value = row, Double = row * 2 })",
            ResultExpressionBuilder.ForCalculatedColumn(
                "new[] { 1, 2 }", table, [0], "Double", "row * 2", 1));
    }

    [Fact]
    public void UnchangedColumnLayoutKeepsTheOriginalExpression()
    {
        var table = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(
            Sequence(Row(("Id", Number("1")), ("Name", Text("Ada"))))));

        Assert.Equal("customers", ResultExpressionBuilder.ForColumns(" customers ", table, [0, 1]));
    }

    [Fact]
    public async Task DictionaryColumnsUseOnlyStandardIndexersAndLinq()
    {
        var dictionaryRow = new ResultSnapshot(
            SnapshotKind.Object,
            "3 entries",
            "System.Collections.Generic.Dictionary`2[System.String,System.Object]",
            Properties:
            [
                new("Id", Number("1")),
                new("display-name", Text("Ada")),
                new("Active", Text("true"))
            ]);
        var table = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(new(
            SnapshotKind.Sequence,
            "1 item",
            "System.Collections.Generic.List`1[System.Collections.Generic.Dictionary`2[System.String,System.Object]]",
            Items: [dictionaryRow])));

        var expression = ResultExpressionBuilder.ForColumns("values", table, [1, 0]);

        Assert.Equal(
            "values.Select(item => new System.Collections.Generic.Dictionary<string, object?> { " +
            "[\"display-name\"] = item[\"display-name\"], [\"Id\"] = item[\"Id\"] })",
            expression);
        Assert.DoesNotContain("PlayGroundSharp", expression, StringComparison.Ordinal);
        var diagnostics = await new CSharpLanguageService().GetDiagnosticsAsync(
            SessionContext.Empty with
            {
                Submissions =
                [
                    "var values = new[] { new Dictionary<string, object?> { " +
                    "[\"Id\"] = 1, [\"display-name\"] = \"Ada\", [\"Active\"] = true } };"
                ]
            },
            expression!);
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public void ShapeSafeFallbackDoesNotClaimToGenerateAPortableProjection()
    {
        var table = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(
            Sequence(Row(("Id", Number("1")), ("Name", Text("Ada"))))));

        Assert.Null(ResultExpressionBuilder.ForColumns("Out[1]", table, [1]));
    }

    [Fact]
    public async Task JsonCellNavigationUsesTheSourceInsteadOfTheAnonymousProjection()
    {
        var jsonObject = new ResultSnapshot(
            SnapshotKind.Json,
            "3 properties",
            "System.Text.Json.Nodes.JsonObject",
            Properties:
            [
                new("k", Number("1")),
                new("a", Number("2")),
                new("ix", Number("3"))
            ]);
        var table = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(jsonObject));
        const string sourceExpression = "json![\"s\"]";

        var projection = ResultExpressionBuilder.ForColumns(sourceExpression, table, [0, 1]);
        var cell = ResultExpressionBuilder.ForCell(sourceExpression, table, table.Rows[0], 0);

        Assert.Equal(
            "new { k = json![\"s\"]![\"k\"], a = json![\"s\"]![\"a\"] }",
            projection);
        Assert.Equal("json![\"s\"]![\"k\"]", cell);
        Assert.DoesNotContain("new {", cell, StringComparison.Ordinal);
        var service = new CSharpLanguageService();
        var context = SessionContext.Empty with
        {
            Submissions = ["JsonNode json = JsonNode.Parse(\"{\\\"s\\\":{\\\"k\\\":1,\\\"a\\\":2,\\\"ix\\\":3}}\")!;"]
        };
        Assert.DoesNotContain(
            await service.GetDiagnosticsAsync(context, projection!),
            static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.DoesNotContain(
            await service.GetDiagnosticsAsync(context, cell!),
            static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }

    [Fact]
    public async Task CellAndFlattenOperationsFollowTheOriginalResult()
    {
        var customers = Sequence(
            Row(("Name", Text("Ada")), ("Orders", Sequence(Row(("Id", Number("101")))))),
            Row(("Name", Text("Grace")), ("Orders", Sequence(Row(("Id", Number("201")))))));
        var table = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(customers));
        var ordersColumn = table.Columns.ToList().IndexOf("Orders");

        var cell = ResultExpressionBuilder.ForCell("customers", table, table.Rows[1], ordersColumn);
        var flattened = ResultExpressionBuilder.ForFlattenedColumn("customers", table, ordersColumn);

        Assert.Equal("customers[1].Orders", cell);
        Assert.Equal("customers.SelectMany(item => item.Orders)", flattened);
        var context = SessionContext.Empty with
        {
            Submissions =
            [
                "var customers = new[] { " +
                "new { Orders = new[] { new { Id = 1 } } }, " +
                "new { Orders = new[] { new { Id = 2 } } } };"
            ]
        };
        var service = new CSharpLanguageService();
        Assert.DoesNotContain(
            await service.GetDiagnosticsAsync(context, cell!),
            static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.DoesNotContain(
            await service.GetDiagnosticsAsync(context, flattened!),
            static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
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
        var firstItem = ResultExpressionBuilder.ForItem("json", jsonArray, 0);

        Assert.Equal("json!.AsArray().SelectMany(item => item![\"order-items\"]!.AsArray())", flattened);
        Assert.Equal("json!.AsArray()[0]", firstItem);
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

    [Fact]
    public void EnumerableOnlyResultsKeepElementAt()
    {
        var customers = new ResultSnapshot(
            SnapshotKind.Sequence,
            "1 item",
            "System.Linq.Enumerable+WhereArrayIterator`1",
            Items: [Row(("Name", Text("Ada")))]);

        var expression = ResultExpressionBuilder.ForItem("customers", customers, 0);

        Assert.Equal("customers.ElementAt(0)", expression);
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
