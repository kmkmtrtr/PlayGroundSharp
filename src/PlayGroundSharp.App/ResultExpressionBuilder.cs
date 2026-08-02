using System.Text;
using PlayGroundSharp.Core;
using PlayGroundSharp.LanguageService;

namespace PlayGroundSharp.App;

internal static class ResultExpressionBuilder
{
    public static string? ForCell(
        string? tableExpression,
        SnapshotTableModel model,
        SnapshotTableRow row,
        int columnIndex)
    {
        if (string.IsNullOrWhiteSpace(tableExpression) ||
            columnIndex < 0 || columnIndex >= model.Columns.Count)
            return null;

        if (RequiresShapeSafeAccess(tableExpression))
        {
            var shapeSafeRowExpression = model.SourceRowsAreItems
                ? $"{ShapeSafeSequence(tableExpression)}.ElementAt({row.SourceIndex})"
                : tableExpression.Trim();
            return model.HasSyntheticValueColumn
                ? shapeSafeRowExpression
                : ShapeSafeProperty(shapeSafeRowExpression, model.Columns[columnIndex]);
        }

        var rowExpression = model.SourceRowsAreItems
            ? $"{AsSequence(tableExpression, model.SourceSnapshot)}.ElementAt({row.SourceIndex})"
            : tableExpression.Trim();
        return model.HasSyntheticValueColumn
            ? rowExpression
            : AppendProperty(rowExpression, row.Source, model.Columns[columnIndex]);
    }

    public static string? ForProperty(
        string? sourceExpression,
        ResultSnapshot source,
        string propertyName) =>
        string.IsNullOrWhiteSpace(sourceExpression)
            ? null
            : RequiresShapeSafeAccess(sourceExpression)
                ? ShapeSafeProperty(sourceExpression, propertyName)
                : AppendProperty(sourceExpression, source, propertyName);

    public static string? ForItem(
        string? sourceExpression,
        ResultSnapshot source,
        int index) =>
        string.IsNullOrWhiteSpace(sourceExpression)
            ? null
            : $"{(RequiresShapeSafeAccess(sourceExpression) ? ShapeSafeSequence(sourceExpression) : AsSequence(sourceExpression, source))}.ElementAt({index})";

    public static string? ForSlice(
        string? sourceExpression,
        ResultSnapshot source,
        int offset,
        int count) =>
        string.IsNullOrWhiteSpace(sourceExpression)
            ? null
            : $"{(RequiresShapeSafeAccess(sourceExpression) ? ShapeSafeSequence(sourceExpression) : AsSequence(sourceExpression, source))}.Skip({offset}).Take({count})";

    public static string? ForFlattenedColumn(
        string? tableExpression,
        SnapshotTableModel model,
        int columnIndex)
    {
        if (string.IsNullOrWhiteSpace(tableExpression) ||
            columnIndex < 0 || columnIndex >= model.Columns.Count)
            return null;

        var profile = model.GetColumnProfile(columnIndex);
        if (profile.SequenceCount == 0) return null;

        if (RequiresShapeSafeAccess(tableExpression))
        {
            if (!model.SourceRowsAreItems)
            {
                var valueExpression = model.HasSyntheticValueColumn
                    ? tableExpression.Trim()
                    : ShapeSafeProperty(tableExpression, model.Columns[columnIndex]);
                return ShapeSafeSequence(valueExpression);
            }

            const string sourceItem = "item";
            var itemValueExpression = model.HasSyntheticValueColumn
                ? sourceItem
                : ShapeSafeProperty(sourceItem, model.Columns[columnIndex]);
            return $"{ShapeSafeSequence(tableExpression)}.SelectMany({sourceItem} => " +
                   $"{ShapeSafeSequence(itemValueExpression)})";
        }

        var sequenceRow = model.Rows.FirstOrDefault(row =>
            model.TryGetCell(row, columnIndex, out var cell) && cell.Source?.Items is not null);
        if (sequenceRow is null ||
            !model.TryGetCell(sequenceRow, columnIndex, out var sequenceCell) ||
            sequenceCell.Source is null)
            return null;

        if (!model.SourceRowsAreItems)
        {
            var cellExpression = model.HasSyntheticValueColumn
                ? tableExpression.Trim()
                : AppendProperty(tableExpression, sequenceRow.Source, model.Columns[columnIndex]);
            return cellExpression is null ? null : AsSequence(cellExpression, sequenceCell.Source);
        }

        const string item = "item";
        var needsShapeSafeProjection =
            profile.ObjectCount > 0 ||
            profile.ScalarCount > 0 ||
            profile.NullCount > 0 ||
            profile.SequenceCount != model.Rows.Count;
        if (needsShapeSafeProjection)
        {
            var valueExpression = model.HasSyntheticValueColumn
                ? item
                : $"PlayGroundSharp.Core.ResultQuery.Property({item}, {QuoteString(model.Columns[columnIndex])})";
            return $"{AsSequence(tableExpression, model.SourceSnapshot)}.SelectMany({item} => " +
                   $"PlayGroundSharp.Core.ResultQuery.Flatten({valueExpression}))";
        }

        var selectedExpression = model.HasSyntheticValueColumn
            ? item
            : AppendProperty(item, sequenceRow.Source, model.Columns[columnIndex]);
        if (selectedExpression is null) return null;

        return $"{AsSequence(tableExpression, model.SourceSnapshot)}.SelectMany({item} => " +
               $"{AsSequence(selectedExpression, sequenceCell.Source)})";
    }

    private static string? AppendProperty(string expression, ResultSnapshot source, string propertyName)
    {
        var literal = QuoteString(propertyName);
        if (IsJsonElement(source)) return $"{CSharpExpressionText.Receiver(expression)}.GetProperty({literal})";
        if (IsJsonNode(source)) return $"{CSharpExpressionText.NullForgivenReceiver(expression)}[{literal}]";
        if (UsesStringIndexer(source)) return $"{CSharpExpressionText.Receiver(expression)}[{literal}]";
        return IsSimpleIdentifier(propertyName)
            ? $"{CSharpExpressionText.Receiver(expression)}.{propertyName}"
            : null;
    }

    private static string AsSequence(string expression, ResultSnapshot snapshot)
    {
        if (IsJsonElement(snapshot)) return $"{CSharpExpressionText.Receiver(expression)}.EnumerateArray()";
        if (IsJsonNode(snapshot)) return $"{CSharpExpressionText.NullForgivenReceiver(expression)}.AsArray()";
        return CSharpExpressionText.Receiver(expression);
    }

    private static bool RequiresShapeSafeAccess(string expression) =>
        expression.TrimStart().StartsWith("Out[", StringComparison.Ordinal) ||
        expression.Contains("PlayGroundSharp.Core.ResultQuery.", StringComparison.Ordinal);

    private static string ShapeSafeProperty(string expression, string propertyName) =>
        $"PlayGroundSharp.Core.ResultQuery.Property((object?){CSharpExpressionText.CastOperand(expression)}, {QuoteString(propertyName)})";

    private static string ShapeSafeSequence(string expression) =>
        $"PlayGroundSharp.Core.ResultQuery.Flatten((object?){CSharpExpressionText.CastOperand(expression)})";

    private static bool IsJsonElement(ResultSnapshot snapshot) =>
        snapshot.Kind == SnapshotKind.Json &&
        string.Equals(snapshot.TypeName, "System.Text.Json.JsonElement", StringComparison.Ordinal);

    private static bool IsJsonNode(ResultSnapshot snapshot) =>
        snapshot.Kind == SnapshotKind.Json &&
        snapshot.TypeName?.StartsWith("System.Text.Json.Nodes.", StringComparison.Ordinal) == true;

    private static bool UsesStringIndexer(ResultSnapshot snapshot) =>
        snapshot.TypeName?.Contains("Dictionary", StringComparison.Ordinal) == true ||
        snapshot.TypeName?.Contains("DataRow", StringComparison.Ordinal) == true;

    private static bool IsSimpleIdentifier(string value) =>
        value.Length > 0 &&
        (char.IsLetter(value[0]) || value[0] == '_') &&
        value.Skip(1).All(static character => char.IsLetterOrDigit(character) || character == '_');

    private static string QuoteString(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ when char.IsControl(character) => $"\\u{(int)character:X4}",
                _ => character.ToString()
            });
        }
        return builder.Append('"').ToString();
    }
}
