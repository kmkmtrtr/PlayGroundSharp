using System.ComponentModel;

namespace PlayGroundSharp.App.Tests;

public sealed class TableSortCycleTests
{
    [Fact]
    public void SortingCyclesThroughAscendingDescendingAndOriginalOrder()
    {
        var ascending = TableSortCycle.Next(TableSortState.Original);
        var descending = TableSortCycle.Next(ascending);
        var original = TableSortCycle.Next(descending);

        Assert.Equal(TableSortState.Ascending, ascending);
        Assert.Equal(ListSortDirection.Ascending, TableSortCycle.ToListSortDirection(ascending));
        Assert.Equal("▲", TableSortCycle.Glyph(ascending));
        Assert.Equal(TableSortState.Descending, descending);
        Assert.Equal(ListSortDirection.Descending, TableSortCycle.ToListSortDirection(descending));
        Assert.Equal("▼", TableSortCycle.Glyph(descending));
        Assert.Equal(TableSortState.Original, original);
        Assert.Null(TableSortCycle.ToListSortDirection(original));
        Assert.Empty(TableSortCycle.Glyph(original));
    }
}
