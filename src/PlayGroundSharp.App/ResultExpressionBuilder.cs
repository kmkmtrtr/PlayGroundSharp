using System.Text;
using PlayGroundSharp.Core;

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

        var rowExpression = model.SourceRowsAreItems
            ? $"{AsSequence(tableExpression, model.SourceSnapshot)}.ElementAt({row.SourceIndex})"
            : tableExpression.Trim();
        return model.HasSyntheticValueColumn
            ? Parenthesize(rowExpression)
            : AppendProperty(rowExpression, row.Source, model.Columns[columnIndex]);
    }

    public static string? ForFlattenedColumn(
        string? tableExpression,
        SnapshotTableModel model,
        int columnIndex)
    {
        if (string.IsNullOrWhiteSpace(tableExpression) ||
            columnIndex < 0 || columnIndex >= model.Columns.Count)
            return null;

        var profile = model.GetColumnProfile(columnIndex);
        if (profile.SequenceCount == 0 ||
            profile.ObjectCount > 0 ||
            profile.ScalarCount > 0 ||
            profile.NullCount > 0 ||
            model.SourceRowsAreItems && profile.SequenceCount != model.Rows.Count)
            return null;

        var sequenceRow = model.Rows.FirstOrDefault(row =>
            model.TryGetCell(row, columnIndex, out var cell) && cell.Source?.Items is not null);
        if (sequenceRow is null ||
            !model.TryGetCell(sequenceRow, columnIndex, out var sequenceCell) ||
            sequenceCell.Source is null)
            return null;

        if (!model.SourceRowsAreItems)
        {
            var cellExpression = model.HasSyntheticValueColumn
                ? Parenthesize(tableExpression)
                : AppendProperty(tableExpression, sequenceRow.Source, model.Columns[columnIndex]);
            return cellExpression is null ? null : AsSequence(cellExpression, sequenceCell.Source);
        }

        const string item = "item";
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
        if (IsJsonElement(source)) return $"{Parenthesize(expression)}.GetProperty({literal})";
        if (IsJsonNode(source)) return $"{NullForgive(expression)}[{literal}]";
        if (UsesStringIndexer(source)) return $"{Parenthesize(expression)}[{literal}]";
        return IsSimpleIdentifier(propertyName)
            ? $"{Parenthesize(expression)}.{propertyName}"
            : null;
    }

    private static string AsSequence(string expression, ResultSnapshot snapshot)
    {
        if (IsJsonElement(snapshot)) return $"{Parenthesize(expression)}.EnumerateArray()";
        if (IsJsonNode(snapshot)) return $"{NullForgive(expression)}.AsArray()";
        return Parenthesize(expression);
    }

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

    private static string Parenthesize(string expression) => $"({expression.Trim()})";
    private static string NullForgive(string expression) => $"({expression.Trim()}!)";

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
