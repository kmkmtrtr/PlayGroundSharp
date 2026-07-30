using System.Collections;
using System.ComponentModel;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App.Tests;

public sealed class SnapshotTableRowComparerTests
{
    [Fact]
    public void NumberColumnsSortByNumericValue()
    {
        var rows = Rows(
            Number("10"),
            Number("-2"),
            Number("1.5"),
            Number("1e2"),
            Number("9007199254740993"),
            Number("9007199254740992"));

        var sorted = Sort(rows, ListSortDirection.Ascending);

        Assert.Equal(
            ["-2", "1.5", "10", "1e2", "9007199254740992", "9007199254740993"],
            sorted.Select(static row => row.Cells[0].Display));
    }

    [Fact]
    public void TextColumnsUseNaturalNumericSegments()
    {
        var rows = Rows(
            Text("item10"),
            Text("item2"),
            Text("item1"),
            Text("item02"),
            Text("Item3"));

        var sorted = Sort(rows, ListSortDirection.Ascending);

        Assert.Equal(
            ["item1", "item2", "item02", "Item3", "item10"],
            sorted.Select(static row => row.Cells[0].Display));
    }

    [Fact]
    public void DescendingSortKeepsNullAndMissingCellsAtTheEnd()
    {
        var rows = new[]
        {
            Row(0, Number("2")),
            Row(1, Null()),
            Row(2, Number("10")),
            new SnapshotTableRow(3, [SnapshotTableCell.Missing])
        };

        var sorted = Sort(rows, ListSortDirection.Descending);

        Assert.Equal([2, 0, 1, 3], sorted.Select(static row => row.SourceIndex));
    }

    [Theory]
    [InlineData("0.00000000000000000000000000001", "1e-28")]
    [InlineData("99999999999999999999999999999999999999", "1e38")]
    [InlineData("-1e1000", "-9e999")]
    public void ArbitrarySizeDecimalComparisonDoesNotLosePrecision(
        string smaller,
        string larger)
    {
        Assert.True(SnapshotTableRowComparer.CompareNumbers(smaller, larger) < 0);
    }

    [Fact]
    public void SpecialFloatingPointValuesHaveAPredictableOrder()
    {
        var rows = Rows(
            Number("NaN"),
            Number("Infinity"),
            Number("-2"),
            Number("-Infinity"));

        var sorted = Sort(rows, ListSortDirection.Ascending);

        Assert.Equal(
            ["-Infinity", "-2", "Infinity", "NaN"],
            sorted.Select(static row => row.Cells[0].Display));
    }

    [Fact]
    public void EqualValuesKeepTheirOriginalRowOrder()
    {
        var rows = new[]
        {
            Row(4, Text("file2")),
            Row(1, Text("file2")),
            Row(3, Text("file2"))
        };

        var sorted = Sort(rows, ListSortDirection.Descending);

        Assert.Equal([1, 3, 4], sorted.Select(static row => row.SourceIndex));
    }

    private static SnapshotTableRow[] Sort(
        IReadOnlyList<SnapshotTableRow> rows,
        ListSortDirection direction)
    {
        var comparer = SnapshotTableRowComparer.Create(rows, 0, direction);
        var sorted = rows.ToArray();
        Array.Sort(sorted, (IComparer)comparer);
        return sorted;
    }

    private static SnapshotTableRow[] Rows(params ResultSnapshot[] values) =>
        values.Select((value, index) => Row(index, value)).ToArray();

    private static SnapshotTableRow Row(int sourceIndex, ResultSnapshot value) =>
        new(sourceIndex, [new(value.Display ?? string.Empty, value.Display ?? string.Empty, value)]);

    private static ResultSnapshot Number(string value) =>
        new(SnapshotKind.Number, value, "System.Decimal");

    private static ResultSnapshot Text(string value) =>
        new(SnapshotKind.String, value, "System.String");

    private static ResultSnapshot Null() =>
        new(SnapshotKind.Null, "null", TypeName: null);
}
