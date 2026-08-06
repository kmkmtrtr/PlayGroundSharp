using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

internal enum TableFilterOperator
{
    Equals,
    NotEquals,
    Contains,
    DoesNotContain,
    StartsWith,
    EndsWith,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    IsEmpty,
    IsNotEmpty
}

internal sealed record TableColumnFilter(TableFilterOperator Operator, string Value)
{
    public bool RequiresValue => Operator is not TableFilterOperator.IsEmpty and
        not TableFilterOperator.IsNotEmpty;

    public bool Matches(SnapshotTableCell cell)
    {
        var isEmpty = cell.Source is null ||
            cell.Source.Kind == SnapshotKind.Null ||
            cell.ExportValue.Length == 0;
        if (Operator == TableFilterOperator.IsEmpty) return isEmpty;
        if (Operator == TableFilterOperator.IsNotEmpty) return !isEmpty;
        if (isEmpty) return false;

        var text = cell.ExportValue;
        var comparison = cell.Source?.Kind == SnapshotKind.Number
            ? SnapshotTableRowComparer.CompareNumbers(text, Value)
            : StringComparer.OrdinalIgnoreCase.Compare(text, Value);
        return Operator switch
        {
            TableFilterOperator.Equals => comparison == 0,
            TableFilterOperator.NotEquals => comparison != 0,
            TableFilterOperator.Contains => text.Contains(Value, StringComparison.OrdinalIgnoreCase),
            TableFilterOperator.DoesNotContain => !text.Contains(Value, StringComparison.OrdinalIgnoreCase),
            TableFilterOperator.StartsWith => text.StartsWith(Value, StringComparison.OrdinalIgnoreCase),
            TableFilterOperator.EndsWith => text.EndsWith(Value, StringComparison.OrdinalIgnoreCase),
            TableFilterOperator.GreaterThan => comparison > 0,
            TableFilterOperator.GreaterThanOrEqual => comparison >= 0,
            TableFilterOperator.LessThan => comparison < 0,
            TableFilterOperator.LessThanOrEqual => comparison <= 0,
            _ => false
        };
    }

    public static bool MatchesRow(
        SnapshotTableRow row,
        IReadOnlyDictionary<int, TableColumnFilter> filters) =>
        filters.All(pair =>
            pair.Key >= 0 &&
            pair.Key < row.Cells.Count &&
            pair.Value.Matches(row.Cells[pair.Key]));
}
