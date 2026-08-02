using System.Collections;
using System.Data;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlayGroundSharp.Core;

/// <summary>Helpers used by expressions generated from captured result views.</summary>
public static class ResultQuery
{
    public static IReadOnlyDictionary<string, object?> Project(object? source, params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var result = new Dictionary<string, object?>(names.Length, StringComparer.Ordinal);
        foreach (var name in names)
        {
            ArgumentNullException.ThrowIfNull(name);
            result[name] = Property(source, name);
        }
        return result;
    }

    public static object? Property(object? source, string name)
    {
        if (source is null) return null;
        if (source is DataRow row)
            return row.Table.Columns.Contains(name) ? row[name] : null;
        if (source is JsonElement element)
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
                ? value
                : null;
        if (source is JsonObject jsonObject)
            return jsonObject.TryGetPropertyValue(name, out var value) ? value : null;
        if (source is IDictionary dictionary)
            return dictionary.Contains(name) ? dictionary[name] : null;

        var property = source.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public);
        return property?.GetIndexParameters().Length == 0
            ? property.GetValue(source)
            : null;
    }

    public static IEnumerable<object?> Flatten(object? value)
    {
        if (value is null) yield break;
        if (value is DataTable table)
        {
            foreach (DataRow row in table.Rows) yield return row;
            yield break;
        }
        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) yield return item;
            else if (element.ValueKind == JsonValueKind.Object)
                yield return element;
            yield break;
        }
        if (value is JsonArray jsonArray)
        {
            foreach (var item in jsonArray) yield return item;
            yield break;
        }
        if (value is JsonObject or IDictionary)
        {
            yield return value;
            yield break;
        }
        if (value is IEnumerable sequence && value is not string)
        {
            foreach (var item in sequence) yield return item;
            yield break;
        }
        if (!IsScalar(value)) yield return value;
    }

    private static bool IsScalar(object value)
    {
        var type = value.GetType();
        return type.IsPrimitive || type.IsEnum ||
               value is string or decimal or DateTime or DateTimeOffset or DateOnly or TimeOnly or
                   TimeSpan or Guid or Uri or Type or Version or StringBuilder or JsonValue or Exception or DBNull;
    }
}
