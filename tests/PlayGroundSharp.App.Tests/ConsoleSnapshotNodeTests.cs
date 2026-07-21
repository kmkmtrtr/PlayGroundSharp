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

        Assert.Equal(root.Preview, root.AccessibleLabel);
        Assert.Equal("hoge: (2) [1, 2]", property.AccessibleLabel);
        Assert.Equal("[0]: 1", firstItem.AccessibleLabel);
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
}
