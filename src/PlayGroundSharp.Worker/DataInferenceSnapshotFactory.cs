using System.Collections;
using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.Worker;

/// <summary>Builds a compact structural snapshot without transferring every source row.</summary>
internal static class DataInferenceSnapshotFactory
{
    private const int MaximumItemsToInspect = 250_000;
    private const int MaximumSchemaNodes = 25_000;

    public static ResultSnapshot Create(
        object? value,
        ResultSnapshotFactory fallback,
        CancellationToken cancellationToken)
    {
        var budget = new SchemaBudget(cancellationToken);
        return value switch
        {
            JsonNode node => CreateJson(JsonSerializer.SerializeToElement(node), budget),
            JsonElement element => CreateJson(element, budget),
            DataTable table => CreateDataTable(table, budget),
            IEnumerable sequence when value is not string && IsMaterializedSequence(value) =>
                CreateSequence(sequence, value.GetType().FullName, budget),
            _ => fallback.Create(value, cancellationToken)
        };
    }

    private static ResultSnapshot CreateJson(JsonElement element, SchemaBudget budget)
    {
        budget.TakeNode();
        return element.ValueKind switch
        {
            JsonValueKind.Object => CreateJsonObject(element, budget),
            JsonValueKind.Array => CreateJsonArray(element, budget),
            JsonValueKind.String => new(SnapshotKind.String, string.Empty, typeof(JsonElement).FullName),
            JsonValueKind.Number => new(SnapshotKind.Number, element.GetRawText(), typeof(JsonElement).FullName),
            JsonValueKind.True => new(SnapshotKind.Boolean, "true", typeof(JsonElement).FullName),
            JsonValueKind.False => new(SnapshotKind.Boolean, "false", typeof(JsonElement).FullName),
            JsonValueKind.Null or JsonValueKind.Undefined => new(SnapshotKind.Null, "null", typeof(JsonElement).FullName),
            _ => new(SnapshotKind.MaxDepth, "unsupported JSON shape", typeof(JsonElement).FullName)
        };
    }

    private static ResultSnapshot CreateJsonObject(JsonElement element, SchemaBudget budget)
    {
        var properties = new List<ResultProperty>();
        foreach (var property in element.EnumerateObject())
        {
            budget.CancellationToken.ThrowIfCancellationRequested();
            if (properties.Count >= MaximumSchemaNodes)
                throw new InvalidDataException($"The inferred data shape exceeded {MaximumSchemaNodes:N0} properties.");
            properties.Add(new(property.Name, CreateJson(property.Value, budget)));
        }
        return new(
            SnapshotKind.Json,
            $"{properties.Count:N0} inferred properties",
            typeof(JsonElement).FullName,
            Properties: properties,
            TotalCount: properties.Count);
    }

    private static ResultSnapshot CreateJsonArray(JsonElement element, SchemaBudget budget)
    {
        ResultSnapshot? merged = null;
        var count = element.GetArrayLength();
        var inspected = 0;
        foreach (var item in element.EnumerateArray())
        {
            budget.CancellationToken.ThrowIfCancellationRequested();
            if (inspected >= MaximumItemsToInspect) break;
            inspected++;
            merged = Merge(merged, CreateJson(item, budget));
        }
        return new(
            SnapshotKind.Json,
            $"{count:N0} items; {inspected:N0} inspected for type inference",
            typeof(JsonElement).FullName,
            Items: merged is null ? [] : [merged],
            IsTruncated: inspected < count,
            TotalCount: count);
    }

    private static ResultSnapshot CreateSequence(
        IEnumerable sequence,
        string? typeName,
        SchemaBudget budget)
    {
        ResultSnapshot? merged = null;
        var inspected = 0;
        var truncated = false;
        foreach (var item in sequence)
        {
            budget.CancellationToken.ThrowIfCancellationRequested();
            if (inspected >= MaximumItemsToInspect)
            {
                truncated = true;
                break;
            }
            inspected++;
            merged = Merge(merged, CreateValue(item, budget));
        }
        var totalCount = TryGetCount(sequence, out var count) ? count : inspected;
        return new(
            SnapshotKind.Sequence,
            $"{inspected:N0} rows inspected for type inference",
            typeName,
            Items: merged is null ? [] : [merged],
            IsTruncated: truncated,
            TotalCount: totalCount);
    }

    private static bool IsMaterializedSequence(object value) =>
        value is ICollection || value.GetType().GetInterfaces().Any(static contract =>
            contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>));

    private static bool TryGetCount(IEnumerable sequence, out int count)
    {
        if (sequence is ICollection collection)
        {
            count = collection.Count;
            return true;
        }
        var contract = sequence.GetType().GetInterfaces().FirstOrDefault(static candidate =>
            candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>));
        if (contract?.GetProperty(nameof(IReadOnlyCollection<object>.Count))?.GetValue(sequence) is int value)
        {
            count = value;
            return true;
        }
        count = 0;
        return false;
    }

    private static ResultSnapshot CreateValue(object? value, SchemaBudget budget)
    {
        budget.TakeNode();
        return value switch
        {
            null or DBNull => new(SnapshotKind.Null, "null", null),
            JsonNode node => CreateJson(JsonSerializer.SerializeToElement(node), budget),
            JsonElement element => CreateJson(element, budget),
            IReadOnlyDictionary<string, string?> values => CreateStringDictionary(values, budget),
            IDictionary dictionary => CreateDictionary(dictionary, budget),
            string => new(SnapshotKind.String, string.Empty, typeof(string).FullName),
            bool => new(SnapshotKind.Boolean, "false", typeof(bool).FullName),
            DateTime or DateTimeOffset or DateOnly or TimeOnly =>
                new(SnapshotKind.DateTime, string.Empty, value.GetType().FullName),
            Guid => new(SnapshotKind.Guid, string.Empty, typeof(Guid).FullName),
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
                new(SnapshotKind.Number, Convert.ToString(value, CultureInfo.InvariantCulture), value.GetType().FullName),
            _ => new(SnapshotKind.MaxDepth, "unsupported inferred value", value.GetType().FullName)
        };
    }

    private static ResultSnapshot CreateStringDictionary(
        IReadOnlyDictionary<string, string?> values,
        SchemaBudget budget) =>
        new(
            SnapshotKind.Object,
            $"{values.Count:N0} inferred properties",
            values.GetType().FullName,
            Properties: values.Select(pair =>
                new ResultProperty(pair.Key, CreateValue(pair.Value, budget))).ToArray(),
            TotalCount: values.Count);

    private static ResultSnapshot CreateDictionary(IDictionary values, SchemaBudget budget)
    {
        var properties = new List<ResultProperty>();
        foreach (DictionaryEntry pair in values)
        {
            if (pair.Key is not string name) continue;
            properties.Add(new(name, CreateValue(pair.Value, budget)));
        }
        return new(
            SnapshotKind.Object,
            $"{properties.Count:N0} inferred properties",
            values.GetType().FullName,
            Properties: properties,
            TotalCount: properties.Count);
    }

    private static ResultSnapshot CreateDataTable(DataTable table, SchemaBudget budget)
    {
        var properties = table.Columns.Cast<DataColumn>()
            .Select(column => new ResultProperty(
                column.ColumnName,
                CreateValue(column.AllowDBNull ? null : GetDefault(column.DataType), budget),
                CSharpTypeExpression.TryCreate(column.DataType),
                !column.DataType.IsValueType,
                IsOptional: column.AllowDBNull))
            .ToArray();
        var row = new ResultSnapshot(
            SnapshotKind.Object,
            $"{properties.Length:N0} inferred columns",
            typeof(DataRow).FullName,
            Properties: properties,
            TotalCount: properties.Length);
        return new(
            SnapshotKind.Sequence,
            $"{table.Rows.Count:N0} rows",
            typeof(DataTable).FullName,
            Items: [row],
            TotalCount: table.Rows.Count);
    }

    private static object? GetDefault(Type type) => type.IsValueType ? Activator.CreateInstance(type) : string.Empty;

    private static ResultSnapshot Merge(ResultSnapshot? left, ResultSnapshot right)
    {
        if (left is null) return right;
        if (left.Kind == SnapshotKind.Null)
            return right with { IsInferredNullable = true };
        if (right.Kind == SnapshotKind.Null)
            return left with { IsInferredNullable = true };
        var nullable = left.IsInferredNullable || right.IsInferredNullable;

        if (left.Properties is not null && right.Properties is not null && left.Kind == right.Kind)
        {
            var rightByName = right.Properties.ToDictionary(property => property.Name, StringComparer.Ordinal);
            var properties = new List<ResultProperty>();
            foreach (var property in left.Properties)
            {
                if (rightByName.Remove(property.Name, out var matching))
                {
                    properties.Add(property with
                    {
                        Value = Merge(property.Value, matching.Value),
                        IsOptional = property.IsOptional || matching.IsOptional
                    });
                }
                else
                    properties.Add(property with { IsOptional = true });
            }
            properties.AddRange(rightByName.Values.Select(property => property with { IsOptional = true }));
            return left with
            {
                Properties = properties,
                TotalCount = properties.Count,
                IsInferredNullable = nullable
            };
        }

        if (left.Items is { Count: > 0 } && right.Items is { Count: > 0 } && left.Kind == right.Kind)
            return left with
            {
                Items = [Merge(left.Items[0], right.Items[0])],
                IsInferredNullable = nullable
            };

        if (left.Kind == right.Kind)
        {
            if (left.Kind == SnapshotKind.Number)
                return WiderNumber(left, right) with { IsInferredNullable = nullable };
            return left with { IsInferredNullable = nullable };
        }

        return new(
            SnapshotKind.MaxDepth,
            "mixed value types",
            null,
            IsTruncated: true,
            IsInferredNullable: nullable);
    }

    private static ResultSnapshot WiderNumber(ResultSnapshot left, ResultSnapshot right)
    {
        static int Rank(ResultSnapshot snapshot)
        {
            var text = snapshot.Display ?? string.Empty;
            if (text.Contains('e', StringComparison.OrdinalIgnoreCase) ||
                snapshot.TypeName is "System.Single" or "System.Double") return 3;
            if (text.Contains('.') || snapshot.TypeName == "System.Decimal") return 2;
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
                value is > int.MaxValue or < int.MinValue) return 1;
            return 0;
        }

        return Rank(left) >= Rank(right) ? left : right;
    }

    private sealed class SchemaBudget(CancellationToken cancellationToken)
    {
        public CancellationToken CancellationToken { get; } = cancellationToken;

        public void TakeNode()
        {
            CancellationToken.ThrowIfCancellationRequested();
        }
    }
}
