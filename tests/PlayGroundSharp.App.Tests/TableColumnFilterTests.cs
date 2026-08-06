using PlayGroundSharp.Core;

namespace PlayGroundSharp.App.Tests;

public sealed class TableColumnFilterTests
{
    [Theory]
    [InlineData((int)TableFilterOperator.Equals, "alpha", true)]
    [InlineData((int)TableFilterOperator.NotEquals, "beta", true)]
    [InlineData((int)TableFilterOperator.Contains, "PH", true)]
    [InlineData((int)TableFilterOperator.DoesNotContain, "zzz", true)]
    [InlineData((int)TableFilterOperator.StartsWith, "al", true)]
    [InlineData((int)TableFilterOperator.EndsWith, "HA", true)]
    public void TextConditionsIgnoreCase(int operatorValue, string value, bool expected)
    {
        var cell = Cell(SnapshotKind.String, "Alpha");

        Assert.Equal(expected, new TableColumnFilter((TableFilterOperator)operatorValue, value).Matches(cell));
    }

    [Theory]
    [InlineData((int)TableFilterOperator.Equals, "2.0", true)]
    [InlineData((int)TableFilterOperator.GreaterThan, "1.5", true)]
    [InlineData((int)TableFilterOperator.GreaterThanOrEqual, "2", true)]
    [InlineData((int)TableFilterOperator.LessThan, "3", true)]
    [InlineData((int)TableFilterOperator.LessThanOrEqual, "2", true)]
    public void NumericConditionsCompareAsNumbers(int operatorValue, string value, bool expected)
    {
        var cell = Cell(SnapshotKind.Number, "2");

        Assert.Equal(expected, new TableColumnFilter((TableFilterOperator)operatorValue, value).Matches(cell));
    }

    [Fact]
    public void EmptyConditionsIncludeMissingNullAndEmptyStrings()
    {
        var empty = new TableColumnFilter(TableFilterOperator.IsEmpty, string.Empty);
        var notEmpty = new TableColumnFilter(TableFilterOperator.IsNotEmpty, string.Empty);

        Assert.True(empty.Matches(SnapshotTableCell.Missing));
        Assert.True(empty.Matches(Cell(SnapshotKind.Null, "null")));
        Assert.True(empty.Matches(Cell(SnapshotKind.String, string.Empty)));
        Assert.True(notEmpty.Matches(Cell(SnapshotKind.String, "value")));
    }

    [Fact]
    public void RowMustMatchEveryColumnFilter()
    {
        var row = new SnapshotTableRow(0, [
            Cell(SnapshotKind.String, "Ada"),
            Cell(SnapshotKind.Number, "36")
        ]);
        var filters = new Dictionary<int, TableColumnFilter>
        {
            [0] = new(TableFilterOperator.Contains, "ad"),
            [1] = new(TableFilterOperator.GreaterThan, "30")
        };

        Assert.True(TableColumnFilter.MatchesRow(row, filters));
        filters[1] = new(TableFilterOperator.LessThan, "30");
        Assert.False(TableColumnFilter.MatchesRow(row, filters));
    }

    private static SnapshotTableCell Cell(SnapshotKind kind, string value) =>
        new(value, value, new(kind, value, kind == SnapshotKind.Number ? "System.Double" : "System.String"));
}
