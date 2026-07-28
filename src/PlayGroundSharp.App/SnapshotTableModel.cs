using System.Text;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

internal sealed record SnapshotTableCell(string Display, string ExportValue, ResultSnapshot? Source)
{
    public static SnapshotTableCell Missing { get; } = new(string.Empty, string.Empty, null);
}

internal sealed class SnapshotTableRow(int sourceIndex, IReadOnlyList<SnapshotTableCell> cells)
{
    public int SourceIndex { get; } = sourceIndex;
    public IReadOnlyList<SnapshotTableCell> Cells { get; } = cells;
}

/// <summary>Projects captured row-shaped snapshots into a bounded, UI-friendly table.</summary>
internal sealed class SnapshotTableModel
{
    private const int MaximumRows = 10_000;
    private const int MaximumColumns = 200;
    private const int MaximumCellPreviewLength = 240;

    private SnapshotTableModel(
        IReadOnlyList<string> columns,
        IReadOnlyList<SnapshotTableRow> rows,
        int? totalRowCount,
        bool rowsTruncated,
        bool columnsTruncated,
        bool preferTableView,
        bool sourceRowsAreItems,
        bool hasSyntheticValueColumn)
    {
        Columns = columns;
        Rows = rows;
        TotalRowCount = totalRowCount;
        RowsTruncated = rowsTruncated;
        ColumnsTruncated = columnsTruncated;
        PreferTableView = preferTableView;
        SourceRowsAreItems = sourceRowsAreItems;
        HasSyntheticValueColumn = hasSyntheticValueColumn;
    }

    public IReadOnlyList<string> Columns { get; }
    public IReadOnlyList<SnapshotTableRow> Rows { get; }
    public int? TotalRowCount { get; }
    public bool RowsTruncated { get; }
    public bool ColumnsTruncated { get; }
    public bool PreferTableView { get; }
    public bool SourceRowsAreItems { get; }
    public bool HasSyntheticValueColumn { get; }

    public bool TryGetCell(
        SnapshotTableRow row,
        int columnIndex,
        out SnapshotTableCell cell)
    {
        if (columnIndex < 0 ||
            columnIndex >= Columns.Count ||
            columnIndex >= row.Cells.Count)
        {
            cell = SnapshotTableCell.Missing;
            return false;
        }
        cell = row.Cells[columnIndex];
        return true;
    }

    public static bool CanCreate(ResultSnapshot snapshot) =>
        snapshot.Items is { Count: > 0 } ||
        snapshot.Properties is { Count: > 0 };

    public static SnapshotTableModel? TryCreate(ResultSnapshot snapshot)
    {
        IReadOnlyList<ResultSnapshot> sourceRows;
        var sourceRowsAreItems = false;
        if (snapshot.Items is { Count: > 0 } items)
        {
            sourceRows = items;
            sourceRowsAreItems = true;
        }
        else if (snapshot.Properties is { Count: > 0 })
        {
            sourceRows = [snapshot];
        }
        else
        {
            return null;
        }

        var displayedRows = sourceRows.Take(MaximumRows).ToArray();
        var rowsAreObjects =
            displayedRows.All(static row => row.Properties is not null) &&
            displayedRows.Any(static row => row.Properties is { Count: > 0 });
        var hasSyntheticValueColumn = sourceRowsAreItems && !rowsAreObjects;

        var columns = new List<string>();
        var columnIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var columnsTruncated = false;
        if (hasSyntheticValueColumn)
        {
            columns.Add("Value");
        }
        else
        {
            foreach (var row in displayedRows)
            {
                foreach (var property in row.Properties!)
                {
                    if (columnIndexes.ContainsKey(property.Name)) continue;
                    if (columns.Count >= MaximumColumns)
                    {
                        columnsTruncated = true;
                        continue;
                    }
                    columnIndexes.Add(property.Name, columns.Count);
                    columns.Add(property.Name);
                }
            }
        }
        if (columns.Count == 0) return null;

        var rows = new List<SnapshotTableRow>(displayedRows.Length);
        for (var sourceIndex = 0; sourceIndex < displayedRows.Length; sourceIndex++)
        {
            var sourceRow = displayedRows[sourceIndex];
            if (hasSyntheticValueColumn)
            {
                rows.Add(new(sourceIndex, [CreateCell(sourceRow)]));
                continue;
            }

            var cells = Enumerable.Repeat(SnapshotTableCell.Missing, columns.Count).ToArray();
            foreach (var property in sourceRow.Properties!)
            {
                if (!columnIndexes.TryGetValue(property.Name, out var columnIndex)) continue;
                cells[columnIndex] = CreateCell(property.Value);
            }
            rows.Add(new(sourceIndex, cells));
        }

        var totalRowCount = snapshot.Items is null
            ? 1
            : snapshot.TotalCount ?? (snapshot.IsTruncated ? null : sourceRows.Count);
        var rowsTruncated = snapshot.Items is not null &&
                            (snapshot.IsTruncated || displayedRows.Length < sourceRows.Count ||
                             totalRowCount is { } total && displayedRows.Length < total);
        return new(
            columns,
            rows,
            totalRowCount,
            rowsTruncated,
            columnsTruncated,
            sourceRowsAreItems && rowsAreObjects,
            sourceRowsAreItems,
            hasSyntheticValueColumn);
    }

    public string FormatDelimited(char delimiter)
    {
        var builder = new StringBuilder();
        AppendDelimitedRow(builder, Columns, delimiter);
        foreach (var row in Rows)
        {
            builder.AppendLine();
            AppendDelimitedRow(builder, row.Cells.Select(static cell => cell.ExportValue), delimiter);
        }
        return builder.ToString();
    }

    private static SnapshotTableCell CreateCell(ResultSnapshot snapshot)
    {
        string exportValue;
        string display;
        if (snapshot.Properties is not null || snapshot.Items is not null)
        {
            display = SnapshotTextFormatter.FormatCompact(snapshot);
            exportValue = display;
        }
        else
        {
            exportValue = snapshot.Display ?? snapshot.Kind.ToString();
            display = snapshot.TypeName == typeof(char).FullName
                ? SnapshotTextFormatter.QuoteCharacter(exportValue)
                : exportValue;
            if (snapshot.IsTruncated)
            {
                display += "…";
                exportValue += "…";
            }
        }

        display = display.Replace("\r\n", " ↵ ", StringComparison.Ordinal)
            .Replace('\r', '↵')
            .Replace('\n', '↵')
            .Replace('\t', '⇥');
        if (display.Length > MaximumCellPreviewLength)
            display = display[..(MaximumCellPreviewLength - 1)] + "…";
        return new(display, exportValue, snapshot);
    }

    private static void AppendDelimitedRow(StringBuilder builder, IEnumerable<string> values, char delimiter)
    {
        var first = true;
        foreach (var value in values)
        {
            if (!first) builder.Append(delimiter);
            first = false;
            AppendDelimitedValue(builder, value, delimiter);
        }
    }

    private static void AppendDelimitedValue(StringBuilder builder, string value, char delimiter)
    {
        if (value.IndexOfAny(['"', '\r', '\n', delimiter]) < 0)
        {
            builder.Append(value);
            return;
        }
        builder.Append('"');
        builder.Append(value.Replace("\"", "\"\"", StringComparison.Ordinal));
        builder.Append('"');
    }
}
