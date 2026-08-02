using System.Text.Json;
using System.Text.Json.Nodes;
using System.Data;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.Core.Tests;

public sealed class ResultQueryTests
{
    [Fact]
    public void PropertyReadsClrDictionaryAndJsonValues()
    {
        var customer = new Customer("Ada");
        var dictionary = new Dictionary<string, object?> { ["Name"] = "Grace" };
        var jsonObject = JsonNode.Parse("{\"Name\":\"Linus\"}")!.AsObject();
        var jsonElement = JsonSerializer.Deserialize<JsonElement>("{\"Name\":\"Margaret\"}");

        Assert.Equal("Ada", ResultQuery.Property(customer, "Name"));
        Assert.Equal("Grace", ResultQuery.Property(dictionary, "Name"));
        Assert.Equal("Linus", Assert.IsAssignableFrom<JsonValue>(ResultQuery.Property(jsonObject, "Name")).GetValue<string>());
        Assert.Equal("Margaret", Assert.IsType<JsonElement>(ResultQuery.Property(jsonElement, "Name")).GetString());
        Assert.Null(ResultQuery.Property(jsonObject, "Missing"));
    }

    [Fact]
    public void FlattenMatchesTableShapeRules()
    {
        var customer = new Customer("Ada");
        var dictionary = new Dictionary<string, object?> { ["Name"] = "Grace" };

        Assert.Equal([1, 2], ResultQuery.Flatten(new[] { 1, 2 }));
        Assert.Equal([customer], ResultQuery.Flatten(customer));
        Assert.Equal([dictionary], ResultQuery.Flatten(dictionary));
        Assert.Empty(ResultQuery.Flatten("text"));
        Assert.Empty(ResultQuery.Flatten(42));
        Assert.Empty(ResultQuery.Flatten(null));
    }

    [Fact]
    public void FlattenHandlesJsonArraysObjectsAndScalars()
    {
        var array = JsonNode.Parse("[1,2]")!.AsArray();
        var objectValue = JsonNode.Parse("{\"id\":1}")!.AsObject();
        var scalar = JsonValue.Create(1);

        Assert.Equal(2, ResultQuery.Flatten(array).Count());
        Assert.Equal([objectValue], ResultQuery.Flatten(objectValue));
        Assert.Empty(ResultQuery.Flatten(scalar));
    }

    [Fact]
    public void DataTablesCanBeTraversedThroughTheFallbackExpressionHelpers()
    {
        var table = new DataTable();
        table.Columns.Add("Name", typeof(string));
        table.Rows.Add("Ada");
        table.Rows.Add("Grace");

        var rows = ResultQuery.Flatten(table).ToArray();

        Assert.Equal(2, rows.Length);
        Assert.Equal("Ada", ResultQuery.Property(rows[0], "Name"));
        Assert.Equal("Grace", ResultQuery.Property(rows[1], "Name"));
    }

    private sealed record Customer(string Name);
}
