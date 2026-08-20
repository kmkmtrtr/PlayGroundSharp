using PlayGroundSharp.Core;

namespace PlayGroundSharp.App.Tests;

public sealed class StreamedResultDisplayTests
{
    [Fact]
    public async Task GroupsCompletionOrderedResultsIntoOneSequenceRow()
    {
        await using var viewModel = new MainViewModel();

        viewModel.ApplyStreamedResult(new(
            3,
            4,
            new(SnapshotKind.String, "fast", "System.String")));
        var firstRoot = Assert.Single(Assert.Single(viewModel.Transcript).SnapshotRoots!);
        firstRoot.IsExpanded = false;
        viewModel.ApplyStreamedResult(new(
            3,
            1,
            new(SnapshotKind.String, "slow", "System.String")));

        var line = Assert.Single(viewModel.Transcript);
        Assert.Equal("(2) [\"fast\", \"slow\"]", line.Text);
        Assert.Equal([4, 1], line.Snapshot?.ItemIndexes);
        Assert.Equal(["fast", "slow"], line.Snapshot?.Items?.Select(static item => item.Display));
        var root = Assert.Single(line.SnapshotRoots!);
        Assert.False(root.IsExpanded);
        Assert.Equal(["[4]: \"fast\"", "[1]: \"slow\""],
            root.Children.Select(static child => child.AccessibleLabel));
    }
}
