using PlayGroundSharp.Core;

namespace PlayGroundSharp.App.Tests;

public sealed class ConsoleSnapshotNodeTests
{
    [Fact]
    public void AccessibleLabelsIncludePropertyNamesAndItemIndexes()
    {
        var snapshot = new ResultSnapshot(
            SnapshotKind.Object,
            "object",
            "Example",
            Properties:
            [
                new("hoge", new(
                    SnapshotKind.Sequence,
                    "2 items",
                    "System.Int32[]",
                    Items:
                    [
                        new(SnapshotKind.Number, "1", "System.Int32"),
                        new(SnapshotKind.Number, "2", "System.Int32")
                    ],
                    TotalCount: 2))
            ]);

        var root = ConsoleSnapshotNode.CreateRoot(snapshot);
        var property = Assert.Single(root.Children);
        var firstItem = property.Children[0];

        Assert.Equal("{hoge: (2) [1, 2]}", root.Preview);
        Assert.Equal(root.Preview, root.AccessibleLabel);
        Assert.Equal("hoge: (2) [1, 2]", property.AccessibleLabel);
        Assert.Equal("[0]: 1", firstItem.AccessibleLabel);
    }

    [Fact]
    public void StreamedSequencesUseTheirOriginalSourceIndexes()
    {
        var snapshot = new ResultSnapshot(
            SnapshotKind.Sequence,
            "2 items",
            null,
            Items:
            [
                new(SnapshotKind.String, "fast", "System.String"),
                new(SnapshotKind.String, "slow", "System.String")
            ],
            TotalCount: 2,
            ItemIndexes: [4, 1]);

        var root = ConsoleSnapshotNode.CreateRoot(snapshot);

        Assert.Equal(["[4]: \"fast\"", "[1]: \"slow\""],
            root.Children.Select(static child => child.AccessibleLabel));
    }

    [Fact]
    public void GroupedStreamedSequencesKeepTheirOriginalSourceIndexes()
    {
        var items = Enumerable.Range(0, 121)
            .Select(index => new ResultSnapshot(SnapshotKind.Number, index.ToString(), "System.Int32"))
            .ToArray();
        var snapshot = new ResultSnapshot(
            SnapshotKind.Sequence,
            "121 items",
            null,
            Items: items,
            TotalCount: items.Length,
            ItemIndexes: Enumerable.Range(0, 121).Reverse().ToArray());

        var groups = ConsoleSnapshotNode.CreateRoot(snapshot).Children;

        Assert.Equal(2, groups.Count);
        Assert.Equal("[120]: 0", groups[0].Children[0].AccessibleLabel);
        Assert.Equal("[20]: 100", groups[1].Children[0].AccessibleLabel);
    }

    [Fact]
    public void CharacterArraysKeepControlAndSurrogateCodeUnitsReadable()
    {
        var snapshot = new ResultSnapshot(
            SnapshotKind.Sequence,
            "4 items",
            "System.Char[]",
            Items:
            [
                new(SnapshotKind.String, "a", "System.Char"),
                new(SnapshotKind.String, "\n", "System.Char"),
                new(SnapshotKind.String, "\uD83D", "System.Char"),
                new(SnapshotKind.String, "\uDE00", "System.Char")
            ],
            TotalCount: 4);

        var root = ConsoleSnapshotNode.CreateRoot(snapshot);

        Assert.Equal("(4) ['a', '\\n', '\\uD83D', '\\uDE00']", root.Preview);
        Assert.DoesNotContain('�', root.Preview);
        Assert.Contains("'\\uD83D'", root.CopyText, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonLikeObjectsPreviewNestedPropertiesAndValues()
    {
        var snapshot = new ResultSnapshot(
            SnapshotKind.Json,
            "4 properties",
            "System.Text.Json.Nodes.JsonObject",
            Properties:
            [
                new("id", new(SnapshotKind.Number, "42", "System.Int32")),
                new("name", new(SnapshotKind.String, "Ada", "System.String")),
                new("profile", new(
                    SnapshotKind.Json,
                    "2 properties",
                    null,
                    Properties:
                    [
                        new("active", new(SnapshotKind.Boolean, "true", "System.Boolean")),
                        new("role", new(SnapshotKind.String, "admin", "System.String"))
                    ])),
                new("scores", new(
                    SnapshotKind.Json,
                    "3 items",
                    null,
                    Items:
                    [
                        new(SnapshotKind.Number, "10", null),
                        new(SnapshotKind.Number, "20", null),
                        new(SnapshotKind.Number, "30", null)
                    ],
                    TotalCount: 3))
            ]);

        var root = ConsoleSnapshotNode.CreateRoot(snapshot);

        Assert.Equal(
            "{id: 42, name: \"Ada\", profile: {active: true, role: \"admin\"}, scores: (3) [10, 20, 30]}",
            root.Preview);
    }

    [Fact]
    public void JsonTextKeepsFullWidthSpacesReadable()
    {
        var snapshot = new ResultSnapshot(
            SnapshotKind.Object,
            "1 property",
            "Example",
            Properties:
            [
                new("full　width", new(SnapshotKind.String, "left　right", "System.String"))
            ]);

        var root = ConsoleSnapshotNode.CreateRoot(snapshot);

        Assert.Contains("\"full　width\": \"left　right\"", root.Preview, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u3000", root.Preview, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\u3000", root.CopyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LongScalarPreviewsKeepTheCompleteCapturedValue()
    {
        var value = new string('x', 2_000);
        var root = ConsoleSnapshotNode.CreateRoot(new ResultSnapshot(
            SnapshotKind.String,
            value,
            "System.String"));

        Assert.Equal($"\"{value}\"", root.Preview);
        Assert.DoesNotContain('…', root.Preview);
    }
}
