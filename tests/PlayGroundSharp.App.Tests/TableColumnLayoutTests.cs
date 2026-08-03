namespace PlayGroundSharp.App.Tests;

public sealed class TableColumnLayoutTests
{
    [Fact]
    public void ColumnsCanBeHiddenMovedAndRestored()
    {
        var layout = new TableColumnLayout(4);

        Assert.True(layout.Hide(1));
        Assert.True(layout.MoveRight(0));
        Assert.Equal([2, 0, 3], layout.VisibleColumnIndexes);
        Assert.Equal([1], layout.HiddenColumnIndexes);

        Assert.True(layout.Show(1));
        Assert.Equal([2, 1, 0, 3], layout.VisibleColumnIndexes);

        layout.Reset();
        Assert.True(layout.IsDefault);
        Assert.Equal([0, 1, 2, 3], layout.VisibleColumnIndexes);
    }

    [Fact]
    public void LastVisibleColumnCannotBeHidden()
    {
        var layout = new TableColumnLayout(2);

        Assert.True(layout.Hide(0));
        Assert.False(layout.Hide(1));
        Assert.Equal([1], layout.VisibleColumnIndexes);
    }

    [Fact]
    public void CloneCanBeChangedIndependently()
    {
        var layout = new TableColumnLayout(3);
        layout.Hide(1);
        var clone = layout.Clone();

        clone.ShowAll();
        clone.MoveRight(0);

        Assert.Equal([0, 2], layout.VisibleColumnIndexes);
        Assert.Equal([1, 0, 2], clone.VisibleColumnIndexes);
    }
}
