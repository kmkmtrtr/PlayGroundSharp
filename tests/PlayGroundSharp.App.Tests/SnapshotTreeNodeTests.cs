using PlayGroundSharp.Core;

namespace PlayGroundSharp.App.Tests;

public sealed class SnapshotTreeNodeTests
{
    [Fact]
    public void StreamedSequenceChildrenUseTheirOriginalSourceIndexes()
    {
        var snapshot = new ResultSnapshot(
            SnapshotKind.Sequence,
            "2 items",
            null,
            Items:
            [
                new(SnapshotKind.Number, "20", "System.Int32"),
                new(SnapshotKind.Number, "10", "System.Int32")
            ],
            TotalCount: 2,
            ItemIndexes: [1, 0]);

        var root = SnapshotTreeNode.CreateRoot(snapshot, AppLanguageMode.English);

        Assert.Equal(["[1]", "[0]"],
            root.Children.Select(static child => child.Label.Split(" = ")[0]));
        Assert.Equal(["$[1]", "$[0]"], root.Children.Select(static child => child.Path));
    }

    [Fact]
    public void GroupedStreamedSequenceChildrenKeepTheirOriginalSourceIndexes()
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

        var groups = SnapshotTreeNode.CreateRoot(snapshot, AppLanguageMode.English).Children;

        Assert.Equal(2, groups.Count);
        Assert.StartsWith("[120] =", groups[0].Children[0].Label, StringComparison.Ordinal);
        Assert.StartsWith("[20] =", groups[1].Children[0].Label, StringComparison.Ordinal);
    }

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
        var matchedNode = root!.Children.Single();
        Assert.Equal("First", matchedNode.Label.Split(" = ")[0]);
        Assert.False(root.IsSearchMatch);
        Assert.True(matchedNode.IsSearchMatch);
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
        Assert.Equal(250, CountSearchMatches(root!));
    }

    [Fact]
    public void LabelsSummarizePropertiesInsteadOfShowingOnlyTheirCount()
    {
        var snapshot = new ResultSnapshot(
            SnapshotKind.Json,
            "3 properties",
            "JsonObject",
            Properties:
            [
                new("id", new(SnapshotKind.Number, "42", "System.Int32")),
                new("name", new(SnapshotKind.String, "Ada", "System.String")),
                new("tags", new(
                    SnapshotKind.Json,
                    "2 items",
                    null,
                    Items:
                    [
                        new(SnapshotKind.String, "admin", null),
                        new(SnapshotKind.String, "owner", null)
                    ],
                    TotalCount: 2))
            ]);

        var root = SnapshotTreeNode.CreateRoot(snapshot, AppLanguageMode.Japanese);

        Assert.Equal("JsonObject = {id: 42, name: \"Ada\", tags: (2) [\"admin\", \"owner\"]}", root.Label);
        Assert.Contains("{id: 42, name: \"Ada\", tags: (2) [\"admin\", \"owner\"]}", root.Detail);
        Assert.DoesNotContain("3 プロパティ", root.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void ChildNodesExposeTheirStructuredSnapshotForAlternateViews()
    {
        var nested = new ResultSnapshot(
            SnapshotKind.Object,
            "1 property",
            "Address",
            Properties: [new("City", new(SnapshotKind.String, "London", "System.String"))]);
        var snapshot = new ResultSnapshot(
            SnapshotKind.Object,
            "1 property",
            "Customer",
            Properties: [new("Address", nested)]);

        var address = Assert.Single(SnapshotTreeNode.CreateRoot(
            snapshot,
            AppLanguageMode.English,
            "customer").Children);

        Assert.Same(nested, address.Snapshot);
        Assert.Equal("$.Address", address.Path);
        Assert.Equal("customer.Address", address.Expression);
    }

    [Fact]
    public void ItemAndFilteredNodesKeepTheirSourceExpression()
    {
        var snapshot = new ResultSnapshot(
            SnapshotKind.Sequence,
            "2 items",
            "Customer[]",
            Items:
            [
                new(SnapshotKind.String, "Ada", "System.String"),
                new(SnapshotKind.String, "Grace", "System.String")
            ]);
        var root = SnapshotTreeNode.CreateFilteredRoot(
            snapshot,
            AppLanguageMode.English,
            "Grace",
            out _,
            out _,
            sourceExpression: "customers");

        var item = Assert.Single(root!.Children);

        Assert.Equal("customers[1]", item.Expression);
    }

    [Fact]
    public void ResultHistoryExpressionUsesShapeSafeTreeNavigation()
    {
        var snapshot = new ResultSnapshot(
            SnapshotKind.Object,
            "1 property",
            "Customer",
            Properties:
            [
                new("Orders", new(
                    SnapshotKind.Sequence,
                    "1 item",
                    "Order[]",
                    Items: [new(SnapshotKind.Number, "1", "System.Int32")]))
            ]);

        var orders = Assert.Single(SnapshotTreeNode.CreateRoot(
            snapshot,
            AppLanguageMode.English,
            "Out[1]").Children);
        var order = Assert.Single(orders.Children);

        Assert.Contains("ResultQuery.Property", orders.Expression, StringComparison.Ordinal);
        Assert.Contains("ResultQuery.Flatten", order.Expression, StringComparison.Ordinal);
        Assert.EndsWith(".ElementAt(0)", order.Expression, StringComparison.Ordinal);
    }

    [Fact]
    public void LongScalarLabelsKeepTheCompleteCapturedValue()
    {
        var value = new string('x', 2_000);
        var root = SnapshotTreeNode.CreateRoot(
            new ResultSnapshot(SnapshotKind.String, value, "System.String"),
            AppLanguageMode.English);

        Assert.Contains(value, root.Label, StringComparison.Ordinal);
        Assert.DoesNotContain('…', root.Label);
    }

    [Fact]
    public void TruncatedNodeCanReplaceItsSnapshotWithFreshDetails()
    {
        var initial = new ResultSnapshot(
            SnapshotKind.Json,
            "10 properties",
            "System.Text.Json.Nodes.JsonObject",
            Properties: Enumerable.Range(1, 6)
                .Select(index => new ResultProperty(
                    $"p{index}",
                    new(SnapshotKind.Number, index.ToString(), "System.Text.Json.Nodes.JsonNode")))
                .ToArray(),
            IsTruncated: true,
            TotalCount: 10);
        var refreshed = initial with
        {
            Properties = Enumerable.Range(1, 10)
                .Select(index => new ResultProperty(
                    $"p{index}",
                    new(SnapshotKind.Number, index.ToString(), "System.Text.Json.Nodes.JsonNode")))
                .ToArray(),
            IsTruncated = false
        };
        var root = SnapshotTreeNode.CreateRoot(initial, AppLanguageMode.English, "rows[0]");

        Assert.True(root.CanRefresh);
        Assert.Equal(6, root.Children.Count);
        Assert.Contains("(+4)", root.Label, StringComparison.Ordinal);

        root.ReplaceSnapshot(refreshed);

        Assert.False(root.CanRefresh);
        Assert.Equal(10, root.Children.Count);
        Assert.Contains("(+4)", root.Label, StringComparison.Ordinal);
        Assert.DoesNotContain("captured", root.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountNodes(SnapshotTreeNode node) =>
        1 + node.Children.Sum(CountNodes);

    private static int CountSearchMatches(SnapshotTreeNode node) =>
        (node.IsSearchMatch ? 1 : 0) + node.Children.Sum(CountSearchMatches);
}
