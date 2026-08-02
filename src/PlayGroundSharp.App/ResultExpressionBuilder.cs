using System.Text;
using PlayGroundSharp.Core;
using PlayGroundSharp.LanguageService;

namespace PlayGroundSharp.App;

internal static class ResultExpressionBuilder
{
    public static string? ForColumns(
        string? tableExpression,
        SnapshotTableModel model,
        IReadOnlyList<int> columnIndexes)
    {
        if (string.IsNullOrWhiteSpace(tableExpression) ||
            columnIndexes.Count == 0 ||
            columnIndexes.Any(index => index < 0 || index >= model.Columns.Count))
            return null;

        if (columnIndexes.SequenceEqual(Enumerable.Range(0, model.Columns.Count)))
            return tableExpression.Trim();
        if (model.HasSyntheticValueColumn)
            return tableExpression.Trim();
        if (RequiresShapeSafeAccess(tableExpression))
            return null;

        var names = columnIndexes.Select(index => model.Columns[index]).ToArray();
        var rowSnapshot = model.Rows.FirstOrDefault()?.Source ?? model.SourceSnapshot;
        if (!model.SourceRowsAreItems)
            return BuildColumnProjection(tableExpression.Trim(), rowSnapshot, names);

        var projection = BuildColumnProjection("item", rowSnapshot, names);
        return projection is null
            ? null
            : $"{AsSequence(tableExpression, model.SourceSnapshot)}.Select(item => {projection})";
    }

    public static string? ForCalculatedColumn(
        string? tableExpression,
        SnapshotTableModel model,
        IReadOnlyList<int> columnIndexes,
        string columnName,
        string formula,
        int insertPosition)
    {
        if (string.IsNullOrWhiteSpace(tableExpression) ||
            string.IsNullOrWhiteSpace(formula) ||
            !IsCSharpIdentifier(columnName) ||
            model.Columns.Contains(columnName, StringComparer.Ordinal) ||
            columnIndexes.Count == 0 ||
            columnIndexes.Any(index => index < 0 || index >= model.Columns.Count) ||
            RequiresShapeSafeAccess(tableExpression))
            return null;

        var names = columnIndexes.Select(index => model.Columns[index]).ToArray();
        var rowSnapshot = model.Rows.FirstOrDefault()?.Source ?? model.SourceSnapshot;
        var projection = BuildCalculatedColumnProjection(
            "row",
            rowSnapshot,
            names,
            columnName,
            formula.Trim(),
            Math.Clamp(insertPosition, 0, names.Length),
            model.HasSyntheticValueColumn);
        if (projection is null) return null;

        return model.SourceRowsAreItems
            ? $"{AsSequence(tableExpression, model.SourceSnapshot)}.Select(row => {projection})"
            : $"new[] {{ {tableExpression.Trim()} }}.Select(row => {projection}).Single()";
    }

    public static string? ForCalculatedColumnFormula(
        SnapshotTableModel model,
        int columnIndex)
    {
        if (columnIndex < 0 || columnIndex >= model.Columns.Count) return null;
        if (model.HasSyntheticValueColumn) return "row";
        var rowSnapshot = model.Rows.FirstOrDefault()?.Source ?? model.SourceSnapshot;
        return AppendProjectionProperty("row", rowSnapshot, model.Columns[columnIndex]);
    }

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
            ? AppendItemAccess(tableExpression, model.SourceSnapshot, row.SourceIndex)
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
            : RequiresShapeSafeAccess(sourceExpression)
                ? $"{ShapeSafeSequence(sourceExpression)}.ElementAt({index})"
                : AppendItemAccess(sourceExpression, source, index);

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

    private static string? BuildColumnProjection(
        string receiver,
        ResultSnapshot source,
        IReadOnlyList<string> names)
    {
        var values = names
            .Select(name => AppendProjectionProperty(receiver, source, name))
            .ToArray();
        if (values.Any(static value => value is null)) return null;
        var projectionValues = values.Select(static value => value!).ToArray();

        if (names.All(IsCSharpIdentifier))
            return $"new {{ {string.Join(", ", names.Select((name, index) => $"{name} = {projectionValues[index]}"))} }}";

        return "new System.Collections.Generic.Dictionary<string, object?> { " +
               string.Join(", ", names.Select((name, index) =>
                   $"[{QuoteString(name)}] = {projectionValues[index]}")) +
               " }";
    }

    private static string? BuildCalculatedColumnProjection(
        string receiver,
        ResultSnapshot source,
        IReadOnlyList<string> names,
        string calculatedColumnName,
        string formula,
        int insertPosition,
        bool hasSyntheticValueColumn)
    {
        var entries = names
            .Select(name => (
                Name: name,
                Value: hasSyntheticValueColumn
                    ? receiver
                    : AppendProjectionProperty(receiver, source, name)))
            .ToList();
        if (entries.Any(static entry => entry.Value is null)) return null;
        entries.Insert(insertPosition, (calculatedColumnName, formula));

        if (entries.All(static entry => IsCSharpIdentifier(entry.Name)))
            return $"new {{ {string.Join(", ", entries.Select(entry => $"{entry.Name} = {entry.Value}"))} }}";

        return "new System.Collections.Generic.Dictionary<string, object?> { " +
               string.Join(", ", entries.Select(entry =>
                   $"[{QuoteString(entry.Name)}] = {entry.Value}")) +
               " }";
    }

    private static string? AppendProjectionProperty(
        string receiver,
        ResultSnapshot source,
        string propertyName)
    {
        if (IsJsonElement(source) || IsJsonNode(source) || UsesStringIndexer(source) ||
            IsCSharpIdentifier(propertyName))
            return AppendProperty(receiver, source, propertyName);

        var operand = CSharpExpressionText.CastOperand(receiver);
        return $"((object?){operand})?.GetType().GetProperty({QuoteString(propertyName)})?.GetValue((object?){operand})";
    }

    private static string AsSequence(string expression, ResultSnapshot snapshot)
    {
        if (IsJsonElement(snapshot)) return $"{CSharpExpressionText.Receiver(expression)}.EnumerateArray()";
        if (IsJsonNode(snapshot)) return $"{CSharpExpressionText.NullForgivenReceiver(expression)}.AsArray()";
        return CSharpExpressionText.Receiver(expression);
    }

    private static string AppendItemAccess(string expression, ResultSnapshot snapshot, int index)
    {
        var sequence = AsSequence(expression, snapshot);
        return SupportsIntegerIndexer(snapshot)
            ? $"{sequence}[{index}]"
            : $"{sequence}.ElementAt({index})";
    }

    private static bool SupportsIntegerIndexer(ResultSnapshot snapshot)
    {
        if (snapshot.Items is not null && IsJsonNode(snapshot)) return true;
        var typeName = snapshot.TypeName;
        if (string.IsNullOrEmpty(typeName)) return false;
        if (typeName.EndsWith("[]", StringComparison.Ordinal)) return true;
        return IntegerIndexerTypePrefixes.Any(prefix =>
            typeName.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static readonly string[] IntegerIndexerTypePrefixes =
    [
        "System.Collections.ArrayList",
        "System.Collections.Generic.List`1",
        "System.Collections.Generic.IList`1",
        "System.Collections.Generic.IReadOnlyList`1",
        "System.Collections.ObjectModel.Collection`1",
        "System.Collections.ObjectModel.ObservableCollection`1",
        "System.Collections.ObjectModel.ReadOnlyCollection`1",
        "System.Collections.Immutable.ImmutableArray`1",
        "System.ArraySegment`1"
    ];

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

    private static bool IsCSharpIdentifier(string value) =>
        IsSimpleIdentifier(value) && !CSharpKeywords.Contains(value);

    private static readonly HashSet<string> CSharpKeywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while"
    ];

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
