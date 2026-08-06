using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows;

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

    [Fact]
    public void LongColumnNameLeavesRoomForTheSortGlyph()
    {
        RunOnStaThread(() =>
        {
            var header = new TableSortHeader(
                "A very long column name that cannot fit",
                "A very long column name that cannot fit")
            {
                Width = 120
            };
            header.SortGlyph.Text = TableSortCycle.Glyph(TableSortState.Ascending);

            header.Measure(new Size(120, 26));
            header.Arrange(new Rect(0, 0, 120, 26));

            Assert.Equal(TextTrimming.CharacterEllipsis, header.Label.TextTrimming);
            Assert.Equal(GridUnitType.Star, header.ColumnDefinitions[0].Width.GridUnitType);
            Assert.Equal(GridUnitType.Auto, header.ColumnDefinitions[1].Width.GridUnitType);
            Assert.Equal(GridUnitType.Auto, header.ColumnDefinitions[2].Width.GridUnitType);
            Assert.Equal(12, header.SortGlyph.ActualWidth);
            Assert.True(header.Label.ActualWidth < header.ActualWidth);
        });
    }

    [Fact]
    public void FilterGlyphKeepsItsOwnHeaderSpace()
    {
        RunOnStaThread(() =>
        {
            var header = new TableSortHeader("Name", "Name") { Width = 120 };
            header.FilterGlyph.Visibility = Visibility.Visible;
            header.Measure(new Size(120, 26));
            header.Arrange(new Rect(0, 0, 120, 26));

            Assert.Equal(10, header.FilterGlyph.ActualWidth);
            Assert.True(header.Label.ActualWidth < header.ActualWidth);
        });
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
