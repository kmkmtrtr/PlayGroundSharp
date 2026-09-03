using PlayGroundSharp.Core;

namespace PlayGroundSharp.App.Tests;

public sealed class VariableItemTests
{
    [Fact]
    public async Task ViewModelCombinesVariablesAndUnnamedResults()
    {
        await using var viewModel = new MainViewModel();
        var value = new ResultSnapshot(SnapshotKind.Number, "42", "System.Int32");
        var items = viewModel.CreateVariableItems(new(
            [new("answer", "System.Int32", value, false, "int")],
            [new(3, "System.Int32", "int", value)]));

        var variable = Assert.Single(items, static item => !item.IsUnnamedResult);
        Assert.Equal("var", variable.Kind);
        Assert.Equal("answer", variable.SourceExpression);
        Assert.Equal("int", variable.TypeExpression);
        var result = Assert.Single(items, static item => item.IsUnnamedResult);
        Assert.Equal("result", result.Kind);
        Assert.Equal("Out[3]", result.SourceExpression);
        Assert.Equal(3, result.SubmissionIndex);
        Assert.Contains("#3", result.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void UnnamedResultSupportsInlineNamingState()
    {
        var value = new ResultSnapshot(SnapshotKind.Object, "{}", "System.Object");
        var item = new VariableItem("(unnamed #1)", "System.Object", "{}", true, value, 1, "object");

        item.PendingName = "result";
        item.IsNaming = true;

        Assert.True(item.IsNaming);
        Assert.Equal("result", item.PendingName);
    }

    [Fact]
    public async Task ViewModelRejectsAnExistingVariableNameBeforeExecution()
    {
        await using var viewModel = new MainViewModel();
        var value = new ResultSnapshot(SnapshotKind.Number, "42", "System.Int32");
        viewModel.VariableItems.Add(new VariableItem("answer", "System.Int32", "42", false, value));

        var accepted = await viewModel.NameRetainedResultAsync(2, "int", "@answer");

        Assert.False(accepted);
        Assert.Contains("answer", viewModel.Status, StringComparison.Ordinal);
    }
}
