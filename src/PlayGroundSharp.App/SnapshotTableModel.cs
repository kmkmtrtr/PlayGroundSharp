using System.Text;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

internal sealed record SnapshotTableCell(string Display, string ExportValue, ResultSnapshot? Source)
{
    public static SnapshotTableCell Missing { get; } = new(string.Empty, string.Empty, null);
    public static SnapshotTableCell Uncaptured { get; } = new("…", string.Empty, null);
}

internal sealed record SnapshotTableRowOrigin(
    int ParentSourceIndex,
    int ParentColumnIndex,
    int? ItemIndex)
{
    public string Display => ItemIndex is { } itemIndex
        ? $"↩ #{ParentSourceIndex + 1} [{itemIndex}]"
        : $"↩ #{ParentSourceIndex + 1}";
}

internal sealed record SnapshotTableColumnProfile(
    int SequenceCount,
    int ObjectCount,
    int ScalarCount,
    int NullCount)
{
    public bool IsMixed =>
        (SequenceCount > 0 ? 1 : 0) +
        (ObjectCount > 0 ? 1 : 0) +
        (ScalarCount > 0 ? 1 : 0) +
        (NullCount > 0 ? 1 : 0) > 1;

    public int ExcludedCount => ScalarCount + NullCount;
}

internal sealed class SnapshotTableRow(
    int sourceIndex,
    ResultSnapshot source,
    IReadOnlyList<SnapshotTableCell> cells,
    SnapshotTableRowOrigin? origin = null)
{
    public SnapshotTableRow(
        int sourceIndex,
        IReadOnlyList<SnapshotTableCell> cells,
        SnapshotTableRowOrigin? origin = null)
        : this(
            sourceIndex,
            cells.FirstOrDefault(static cell => cell.Source is not null)?.Source ??
            new ResultSnapshot(SnapshotKind.Null, "null", TypeName: null),
            cells,
            origin)
    {
    }

    public int SourceIndex { get; } = sourceIndex;
    public ResultSnapshot Source { get; } = source;
    public IReadOnlyList<SnapshotTableCell> Cells { get; } = cells;
    public SnapshotTableRowOrigin? Origin { get; } = origin;
}

/// <summary>Projects captured row-shaped snapshots into a bounded, UI-friendly table.</summary>
internal sealed class SnapshotTableModel
{
    private const int MaximumRows = 10_000;
    private const int MaximumColumns = 200;

    private SnapshotTableModel(
        ResultSnapshot sourceSnapshot,
        IReadOnlyList<string> columns,
        IReadOnlyList<SnapshotTableRow> rows,
        int? totalRowCount,
        bool rowsTruncated,
        bool columnsTruncated,
        bool preferTableView,
        bool sourceRowsAreItems,
        bool hasSyntheticValueColumn,
        SnapshotTableColumnProfile? flattenedColumnProfile = null)
    {
        SourceSnapshot = sourceSnapshot;
        Columns = columns;
        Rows = rows;
        TotalRowCount = totalRowCount;
        RowsTruncated = rowsTruncated;
        ColumnsTruncated = columnsTruncated;
        PreferTableView = preferTableView;
        SourceRowsAreItems = sourceRowsAreItems;
        HasSyntheticValueColumn = hasSyntheticValueColumn;
        FlattenedColumnProfile = flattenedColumnProfile;
    }

    public ResultSnapshot SourceSnapshot { get; }
    public IReadOnlyList<string> Columns { get; }
    public IReadOnlyList<SnapshotTableRow> Rows { get; }
    public int? TotalRowCount { get; }
    public bool RowsTruncated { get; }
    public bool ColumnsTruncated { get; }
    public bool PreferTableView { get; }
    public bool SourceRowsAreItems { get; }
    public bool HasSyntheticValueColumn { get; }
    public SnapshotTableColumnProfile? FlattenedColumnProfile { get; }
    public bool HasRowOrigins => Rows.Any(static row => row.Origin is not null);

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
                rows.Add(new(sourceIndex, sourceRow, [CreateCell(sourceRow)]));
                continue;
            }

            var cells = Enumerable.Repeat(
                sourceRow.IsTruncated ? SnapshotTableCell.Uncaptured : SnapshotTableCell.Missing,
                columns.Count).ToArray();
            foreach (var property in sourceRow.Properties!)
            {
                if (!columnIndexes.TryGetValue(property.Name, out var columnIndex)) continue;
                cells[columnIndex] = CreateCell(property.Value);
            }
            rows.Add(new(sourceIndex, sourceRow, cells));
        }

        var totalRowCount = snapshot.Items is null
            ? 1
            : snapshot.TotalCount ?? (snapshot.IsTruncated ? null : sourceRows.Count);
        var rowsTruncated = snapshot.Items is not null &&
                            (snapshot.IsTruncated || displayedRows.Length < sourceRows.Count ||
                             totalRowCount is { } total && displayedRows.Length < total);
        return new(
            snapshot,
            columns,
            rows,
            totalRowCount,
            rowsTruncated,
            columnsTruncated,
            sourceRowsAreItems && rowsAreObjects,
            sourceRowsAreItems,
            hasSyntheticValueColumn);
    }

    public SnapshotTableColumnProfile GetColumnProfile(int columnIndex)
    {
        if (columnIndex < 0 || columnIndex >= Columns.Count)
            return new(0, 0, 0, 0);

        var sequenceCount = 0;
        var objectCount = 0;
        var scalarCount = 0;
        var nullCount = 0;
        foreach (var row in Rows)
        {
            if (!TryGetCell(row, columnIndex, out var cell) || cell.Source is not { } source)
                continue;

            if (source.Items is not null) sequenceCount++;
            else if (source.Properties is not null) objectCount++;
            else if (source.Kind == SnapshotKind.Null) nullCount++;
            else scalarCount++;
        }
        return new(sequenceCount, objectCount, scalarCount, nullCount);
    }

    public SnapshotTableModel? TryCreateFlattenedColumn(int columnIndex)
    {
        var profile = GetColumnProfile(columnIndex);
        if (profile.SequenceCount == 0) return null;

        var flattenedItems = new List<ResultSnapshot>();
        var origins = new List<SnapshotTableRowOrigin>();
        var rowsTruncated = RowsTruncated;
        long knownTotalCount = 0;
        var totalCountIsKnown = !RowsTruncated;
        foreach (var row in Rows)
        {
            if (!TryGetCell(row, columnIndex, out var cell) || cell.Source is not { } source)
                continue;

            if (source.Items is { } items)
            {
                var availableCapacity = MaximumRows - flattenedItems.Count;
                var capturedCount = Math.Min(items.Count, Math.Max(availableCapacity, 0));
                for (var itemIndex = 0; itemIndex < capturedCount; itemIndex++)
                {
                    flattenedItems.Add(items[itemIndex]);
                    origins.Add(new(row.SourceIndex, columnIndex, itemIndex));
                }
                if (items.Count > availableCapacity) rowsTruncated = true;

                if (source.TotalCount is { } sourceTotalCount)
                {
                    knownTotalCount += sourceTotalCount;
                }
                else if (!source.IsTruncated)
                {
                    knownTotalCount += items.Count;
                }
                else
                {
                    totalCountIsKnown = false;
                }
                rowsTruncated |= source.IsTruncated;
            }
            else if (source.Properties is not null)
            {
                knownTotalCount++;
                if (flattenedItems.Count < MaximumRows)
                {
                    flattenedItems.Add(source);
                    origins.Add(new(row.SourceIndex, columnIndex, ItemIndex: null));
                }
                else
                {
                    rowsTruncated = true;
                }
            }
        }

        if (flattenedItems.Count == 0) return null;

        var totalCount = totalCountIsKnown && knownTotalCount <= int.MaxValue
            ? (int?)knownTotalCount
            : null;
        rowsTruncated |= totalCount is { } total && total > flattenedItems.Count;
        var flattenedSnapshot = new ResultSnapshot(
            SnapshotKind.Sequence,
            $"{flattenedItems.Count} items",
            TypeName: null,
            Items: flattenedItems,
            IsTruncated: rowsTruncated,
            TotalCount: totalCount);
        var flattenedModel = TryCreate(flattenedSnapshot);
        if (flattenedModel is null) return null;

        var rows = flattenedModel.Rows
            .Select((row, index) => new SnapshotTableRow(
                row.SourceIndex,
                row.Source,
                row.Cells,
                origins[index]))
            .ToArray();
        return new(
            flattenedSnapshot,
            flattenedModel.Columns,
            rows,
            flattenedModel.TotalRowCount,
            flattenedModel.RowsTruncated,
            flattenedModel.ColumnsTruncated,
            flattenedModel.PreferTableView,
            flattenedModel.SourceRowsAreItems,
            flattenedModel.HasSyntheticValueColumn,
            profile);
    }

    public bool CanFlattenColumn(int columnIndex)
    {
        var profile = GetColumnProfile(columnIndex);
        return profile.SequenceCount > 0 &&
               Rows.Any(row =>
                   TryGetCell(row, columnIndex, out var cell) &&
                   (cell.Source?.Items is { Count: > 0 } ||
                    cell.Source?.Properties is { Count: > 0 }));
    }

    public string FormatDelimited(char delimiter)
        => FormatDelimited(delimiter, Enumerable.Range(0, Columns.Count).ToArray(), Rows);

    public string FormatDelimited(
        char delimiter,
        IReadOnlyList<int> columnIndexes,
        IEnumerable<SnapshotTableRow> rows)
    {
        ArgumentNullException.ThrowIfNull(columnIndexes);
        ArgumentNullException.ThrowIfNull(rows);
        if (columnIndexes.Any(index => index < 0 || index >= Columns.Count))
            throw new ArgumentOutOfRangeException(nameof(columnIndexes));

        var builder = new StringBuilder();
        AppendDelimitedRow(builder, columnIndexes.Select(index => Columns[index]), delimiter);
        foreach (var row in rows)
        {
            builder.AppendLine();
            AppendDelimitedRow(builder, columnIndexes.Select(index => row.Cells[index].ExportValue), delimiter);
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
