using PlayGroundSharp.Core;

namespace PlayGroundSharp.App.Tests;

public sealed class SnapshotTreeNodeTests
{
    [Fact]
    public void FilterMaterializesEveryMatchBelowTheLimit()
    {
        var snapshot = new ResultSnapshot(
            SnapshotKind.Object,
            "2 properties",
            "Sample",
            Properties:
            [
                new("First", new(SnapshotKind.String, "needle", "System.String")),
                new("Second", new(SnapshotKind.String, "other", "System.String"))
            ]);

        var root = SnapshotTreeNode.CreateFilteredRoot(
            snapshot,
            AppLanguageMode.English,
            "needle",
            out var totalMatches,
            out var displayedMatches);

        Assert.NotNull(root);
        Assert.Equal(1, totalMatches);
        Assert.Equal(totalMatches, displayedMatches);
        Assert.Equal("First", root!.Children.Single().Label.Split(" = ")[0]);
    }

    [Fact]
    public void FilterCountsAllMatchesButBoundsMaterializedTree()
    {
        var items = Enumerable.Range(1, 1_000)
            .Select(value => new ResultSnapshot(SnapshotKind.Number, value.ToString(), "System.Int32"))
            .ToArray();
        var snapshot = new ResultSnapshot(
            SnapshotKind.Sequence,
            "1,000 items",
            "System.Int32[]",
            Items: items,
            TotalCount: items.Length);

        var root = SnapshotTreeNode.CreateFilteredRoot(
            snapshot,
            AppLanguageMode.Japanese,
            "System",
            out var totalMatches,
            out var displayedMatches);

        Assert.NotNull(root);
        Assert.Equal(1_001, totalMatches);
        Assert.Equal(250, displayedMatches);
        Assert.InRange(CountNodes(root!), 250, 300);
    }

    private static int CountNodes(SnapshotTreeNode node) =>
        1 + node.Children.Sum(CountNodes);
}
