using System.Text.Json.Nodes;
using System.Dynamic;
using System.Numerics;
using PlayGroundSharp.Core;
using PlayGroundSharp.Worker;

namespace PlayGroundSharp.Worker.Tests;

public sealed class ResultSnapshotFactoryTests
{
    private readonly ResultSnapshotFactory factory = new();

    [Fact]
    public void HandlesScalarObjectSequenceJsonAndNull()
    {
        Assert.Equal(SnapshotKind.Null, factory.Create(null).Kind);
        Assert.Equal(SnapshotKind.Number, factory.Create(42).Kind);
        Assert.Equal(SnapshotKind.String, factory.Create("text").Kind);
        Assert.Equal(SnapshotKind.Object, factory.Create(new { Name = "Ada", Age = 30 }).Kind);
        Assert.Equal(SnapshotKind.Sequence, factory.Create(Enumerable.Range(1, 3)).Kind);
        Assert.Equal(SnapshotKind.Json, factory.Create(JsonNode.Parse("{\"answer\":42}")).Kind);
    }

    [Fact]
    public void CapturesCharactersAndCharacterArraysAsReadableScalars()
    {
        var character = factory.Create('あ');
        var characters = factory.Create("abc".ToCharArray());
        var highSurrogate = factory.Create('\uD83D');

        Assert.Equal(SnapshotKind.String, character.Kind);
        Assert.Equal("System.Char", character.TypeName);
        Assert.Equal("あ", character.Display);
        Assert.Equal("\\uD83D", highSurrogate.Display);
        Assert.Equal(SnapshotKind.Sequence, characters.Kind);
        var items = Assert.IsAssignableFrom<IReadOnlyList<ResultSnapshot>>(characters.Items);
        Assert.Equal(["a", "b", "c"], items.Select(static item => item.Display));
        Assert.All(items, static item => Assert.Equal(SnapshotKind.String, item.Kind));
    }

    [Fact]
    public void PreservesMultidimensionalArrayShape()
    {
        var snapshot = factory.Create(new[,] { { 1, 2, 3 }, { 4, 5, 6 } });

        Assert.Equal(SnapshotKind.Sequence, snapshot.Kind);
        Assert.Equal(2, snapshot.TotalCount);
        var rows = Assert.IsAssignableFrom<IReadOnlyList<ResultSnapshot>>(snapshot.Items);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, static row => Assert.Equal(3, row.TotalCount));
        Assert.Equal(["1", "2", "3"], rows[0].Items!.Select(static item => item.Display));
        Assert.Equal(["4", "5", "6"], rows[1].Items!.Select(static item => item.Display));

        using var document = System.Text.Json.JsonDocument.Parse(SnapshotJsonFormatter.Format(snapshot));
        Assert.Equal(6, document.RootElement[1][2].GetInt32());
    }

    [Fact]
    public void StringKeyedDictionariesUseNamedProperties()
    {
        var snapshot = factory.Create(new Dictionary<string, object?>
        {
            ["answer"] = 42,
            ["items"] = new[] { 1, 2 },
            ["empty"] = null
        });

        Assert.Equal(SnapshotKind.Object, snapshot.Kind);
        Assert.Equal(3, snapshot.TotalCount);
        var properties = Assert.IsAssignableFrom<IReadOnlyList<ResultProperty>>(snapshot.Properties);
        Assert.Equal(["answer", "items", "empty"], properties.Select(static property => property.Name));
        Assert.Equal("42", properties[0].Value.Display);
        Assert.Equal(["1", "2"], properties[1].Value.Items!.Select(static item => item.Display));

        using var document = System.Text.Json.JsonDocument.Parse(SnapshotJsonFormatter.Format(snapshot));
        Assert.Equal(42, document.RootElement.GetProperty("answer").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("items")[1].GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, document.RootElement.GetProperty("empty").ValueKind);
    }

    [Fact]
    public void CapturesTupleAndPublicFieldValues()
    {
        var tuple = factory.Create((Name: "Ada", Age: 30));
        var fields = factory.Create(new PublicFieldContainer { Label = "answer", Value = 42 });

        var tupleMembers = Assert.IsAssignableFrom<IReadOnlyList<ResultProperty>>(tuple.Properties);
        var fieldMembers = Assert.IsAssignableFrom<IReadOnlyList<ResultProperty>>(fields.Properties);
        Assert.Equal(["Item1", "Item2"], tupleMembers.Select(static property => property.Name));
        Assert.Equal(["Ada", "30"], tupleMembers.Select(static property => property.Value.Display));
        Assert.Equal(["Label", "Value"], fieldMembers.Select(static property => property.Name));
        Assert.Equal(["answer", "42"], fieldMembers.Select(static property => property.Value.Display));
    }

    [Fact]
    public void ExpandoObjectsUseNamedProperties()
    {
        dynamic value = new ExpandoObject();
        value.Name = "Ada";
        value.Score = 99;

        var snapshot = factory.Create((object)value);

        Assert.Equal(SnapshotKind.Object, snapshot.Kind);
        var properties = Assert.IsAssignableFrom<IReadOnlyList<ResultProperty>>(snapshot.Properties);
        Assert.Equal(["Name", "Score"], properties.Select(static property => property.Name));
        Assert.Equal(["Ada", "99"], properties.Select(static property => property.Value.Display));
        Assert.Equal(2, snapshot.TotalCount);
    }

    [Fact]
    public void CommonFrameworkValuesStayReadableScalars()
    {
        var date = factory.Create(new DateOnly(2026, 7, 22));
        var time = factory.Create(new TimeOnly(12, 34, 56));
        var duration = factory.Create(new TimeSpan(1, 2, 3, 4, 5));
        var uri = factory.Create(new Uri("https://example.test/日本語", UriKind.Absolute));
        var half = factory.Create((Half)1.5);
        var integer = factory.Create(BigInteger.Parse("123456789012345678901234567890"));

        Assert.Equal("2026-07-22", date.Display);
        Assert.Equal("12:34:56.0000000", time.Display);
        Assert.Equal("1.02:03:04.0050000", duration.Display);
        Assert.Equal("https://example.test/日本語", uri.Display);
        Assert.Equal(SnapshotKind.Number, half.Kind);
        Assert.Equal("1.5", half.Display);
        Assert.Equal(SnapshotKind.Number, integer.Kind);
        Assert.Equal("123456789012345678901234567890", integer.Display);
    }

    [Fact]
    public void TypeVersionAndStringBuilderStayReadableScalars()
    {
        var type = factory.Create(typeof(Dictionary<string, int>));
        var version = factory.Create(new Version(10, 2, 3, 4));
        var builder = factory.Create(new System.Text.StringBuilder("abcdef"), 10, 3);

        Assert.Equal(SnapshotKind.String, type.Kind);
        Assert.StartsWith("System.Collections.Generic.Dictionary`2", type.Display, StringComparison.Ordinal);
        Assert.Contains("System.String", type.Display, StringComparison.Ordinal);
        Assert.Equal("10.2.3.4", version.Display);
        Assert.Equal("abc", builder.Display);
        Assert.True(builder.IsTruncated);
    }

    [Fact]
    public void DetectsCyclesAndMaximumItems()
    {
        var node = new Node();
        node.Next = node;
        var cycle = factory.Create(node);
        var sequence = factory.Create(Enumerable.Range(1, ResultSnapshotFactory.MaximumItems + 1));

        Assert.Equal(SnapshotKind.Circular, cycle.Properties?.Single().Value.Kind);
        Assert.Equal(ResultSnapshotFactory.MaximumItems, sequence.Items?.Count);
        Assert.True(sequence.IsTruncated);
    }

    [Fact]
    public void PreservesJsonAndNestedArrayStructure()
    {
        var snapshot = factory.Create(JsonNode.Parse("{\"name\":\"Ada\",\"matrix\":[[1,2],[3,4]]}"));

        Assert.Equal(SnapshotKind.Json, snapshot.Kind);
        Assert.Equal(2, snapshot.TotalCount);
        var matrix = Assert.Single(snapshot.Properties!, static property => property.Name == "matrix").Value;
        Assert.Equal(2, matrix.Items?.Count);
        Assert.Equal(["1", "2"], matrix.Items![0].Items!.Select(static item => item.Display));
        Assert.False(snapshot.IsTruncated);
    }

    [Fact]
    public void SnapshotsExceptions()
    {
        var snapshot = factory.Create(new InvalidOperationException("boom"));
        Assert.Equal(SnapshotKind.Exception, snapshot.Kind);
        Assert.Contains("boom", snapshot.Display);
    }

    [Fact]
    public void LimitsStringsAndObjectDepth()
    {
        var longString = factory.Create(new string('x', ResultSnapshotFactory.MaximumStringLength + 1));
        var root = new Node();
        var current = root;
        for (var index = 0; index < ResultSnapshotFactory.MaximumDepth + 1; index++)
        {
            current.Next = new Node();
            current = current.Next;
        }
        var deep = factory.Create(root);

        Assert.True(longString.IsTruncated);
        Assert.Equal(ResultSnapshotFactory.MaximumStringLength, longString.Display?.Length);
        Assert.True(ContainsKind(deep, SnapshotKind.MaxDepth));
    }

    private static bool ContainsKind(ResultSnapshot snapshot, SnapshotKind kind) =>
        snapshot.Kind == kind || snapshot.Properties?.Any(property => ContainsKind(property.Value, kind)) == true;

    private sealed class Node
    {
        public Node? Next { get; set; }
    }

    private sealed class PublicFieldContainer
    {
        public string Label = string.Empty;
        public int Value;
    }
}
