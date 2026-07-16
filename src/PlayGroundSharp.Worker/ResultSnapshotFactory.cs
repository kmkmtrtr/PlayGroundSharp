using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.Worker;

/// <summary>Creates bounded snapshots without leaking live Worker objects across IPC.</summary>
public sealed class ResultSnapshotFactory
{
    public const int MaximumDepth = 10;
    public const int MaximumItems = 10_000;
    public const int MaximumNodes = 50_000;
    public const int MaximumStringLength = 10 * 1024 * 1024;
    public const int MaximumTextCharacters = 10 * 1024 * 1024;
    public const int MaximumExceptionTextLength = 64 * 1024;
    private const int MaximumExceptionDepth = 8;

    public ResultSnapshot Create(object? value) => Create(
        value,
        0,
        new HashSet<object>(ReferenceComparer.Instance),
        new SnapshotBudget(MaximumNodes, MaximumTextCharacters));

    public ResultSnapshot Create(object? value, int maximumNodes, int maximumTextCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumNodes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTextCharacters);
        return Create(
            value,
            0,
            new HashSet<object>(ReferenceComparer.Instance),
            new SnapshotBudget(maximumNodes, maximumTextCharacters));
    }

    public static ExceptionInfo CreateException(Exception exception) => CreateException(exception, 0);

    private static ExceptionInfo CreateException(Exception exception, int depth) => new(
        exception.GetType().FullName ?? exception.GetType().Name,
        Truncate(exception.Message, MaximumExceptionTextLength),
        exception.StackTrace is null ? null : Truncate(exception.StackTrace, MaximumExceptionTextLength),
        depth >= MaximumExceptionDepth || exception.InnerException is null
            ? null
            : CreateException(exception.InnerException, depth + 1));

    private ResultSnapshot Create(object? value, int depth, HashSet<object> path, SnapshotBudget budget)
    {
        if (!budget.TryTakeNode())
            return new(SnapshotKind.MaxDepth, "… snapshot limit reached", null, IsTruncated: true);
        if (value is null)
        {
            return new(SnapshotKind.Null, "null", null);
        }

        var type = value.GetType();
        var typeName = type.FullName ?? type.Name;
        if (value is string text)
        {
            var display = budget.TakeText(text, MaximumStringLength, out var truncated);
            return new(SnapshotKind.String, display, typeName, IsTruncated: truncated);
        }
        if (value is bool boolean)
        {
            return new(SnapshotKind.Boolean, boolean ? "true" : "false", typeName);
        }
        if (IsNumber(type))
        {
            return new(SnapshotKind.Number, Convert.ToString(value, CultureInfo.InvariantCulture), typeName);
        }
        if (type.IsEnum)
        {
            return new(SnapshotKind.Enum, value.ToString(), typeName);
        }
        if (value is DateTime or DateTimeOffset)
        {
            return new(SnapshotKind.DateTime, ((IFormattable)value).ToString("O", CultureInfo.InvariantCulture), typeName);
        }
        if (value is Guid guid)
        {
            return new(SnapshotKind.Guid, guid.ToString("D"), typeName);
        }
        if (value is JsonElement element)
        {
            return CreateJsonElement(element, typeName, depth, budget, nodeAlreadyTaken: true);
        }
        if (value is JsonNode node)
        {
            return CreateJsonElement(JsonSerializer.SerializeToElement(node), typeName, depth, budget, nodeAlreadyTaken: true);
        }
        if (value is Exception exception)
        {
            var info = CreateException(exception);
            var display = budget.TakeText(
                $"{info.TypeName}: {info.Message}",
                MaximumExceptionTextLength,
                out var truncated);
            return new(SnapshotKind.Exception, display, typeName, IsTruncated: truncated);
        }
        if (depth >= MaximumDepth)
        {
            return new(SnapshotKind.MaxDepth, "…", typeName, IsTruncated: true);
        }

        var track = !type.IsValueType;
        if (track && !path.Add(value))
        {
            return new(SnapshotKind.Circular, "↻ circular reference", typeName, IsTruncated: true);
        }

        try
        {
            if (value is IEnumerable enumerable)
            {
                return CreateSequence(enumerable, typeName, depth, path, budget);
            }

            var properties = new List<ResultProperty>();
            var readableProperties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(static property => property.GetIndexParameters().Length == 0)
                .ToArray();
            foreach (var property in readableProperties)
            {
                if (!budget.CanTakeNode) break;
                try
                {
                    properties.Add(new(property.Name, Create(property.GetValue(value), depth + 1, path, budget)));
                }
                catch (Exception error)
                {
                    if (budget.TryTakeNode())
                        properties.Add(new(property.Name,
                            CreateExceptionSnapshot(error, property.PropertyType.FullName, budget)));
                }
            }

            return new(
                SnapshotKind.Object,
                $"{readableProperties.Length} properties",
                typeName,
                properties,
                IsTruncated: properties.Count < readableProperties.Length,
                TotalCount: readableProperties.Length);
        }
        finally
        {
            if (track)
            {
                path.Remove(value);
            }
        }
    }

    private ResultSnapshot CreateSequence(
        IEnumerable enumerable,
        string typeName,
        int depth,
        HashSet<object> path,
        SnapshotBudget budget)
    {
        var items = new List<ResultSnapshot>();
        var totalCount = enumerable is ICollection collection ? collection.Count : (int?)null;
        IEnumerator enumerator;
        try
        {
            enumerator = enumerable.GetEnumerator();
        }
        catch (Exception error)
        {
            return CreateExceptionSnapshot(error, typeName, budget);
        }
        try
        {
            var reachedEnd = false;
            var enumerationFailed = false;
            while (items.Count < MaximumItems && budget.CanTakeNode)
            {
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        reachedEnd = true;
                        break;
                    }
                    items.Add(Create(enumerator.Current, depth + 1, path, budget));
                }
                catch (Exception error)
                {
                    if (budget.TryTakeNode()) items.Add(CreateExceptionSnapshot(error, null, budget));
                    enumerationFailed = true;
                    break;
                }
            }
            var truncated = totalCount is { } count
                ? enumerationFailed || items.Count < count
                : enumerationFailed || !reachedEnd;
            return new(
                SnapshotKind.Sequence,
                totalCount is { } knownCount ? $"{knownCount:N0} items" : $"{items.Count:N0} captured items",
                typeName,
                Items: items,
                IsTruncated: truncated,
                TotalCount: totalCount);
        }
        finally
        {
            try
            {
                (enumerator as IDisposable)?.Dispose();
            }
            catch
            {
                // A broken enumerator must not invalidate the accepted submission.
            }
        }
    }

    private static ResultSnapshot CreateExceptionSnapshot(Exception error, string? typeName, SnapshotBudget budget)
    {
        var actual = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
        var display = budget.TakeText(
            $"{actual.GetType().Name}: {actual.Message}",
            MaximumExceptionTextLength,
            out var truncated);
        return new(
            SnapshotKind.Exception,
            display,
            typeName ?? actual.GetType().FullName,
            IsTruncated: truncated);
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private ResultSnapshot CreateJsonElement(
        JsonElement element,
        string typeName,
        int depth,
        SnapshotBudget budget,
        bool nodeAlreadyTaken = false)
    {
        if (!nodeAlreadyTaken && !budget.TryTakeNode())
            return new(SnapshotKind.MaxDepth, "… snapshot limit reached", typeName, IsTruncated: true);
        if (depth >= MaximumDepth && element.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            return new(SnapshotKind.MaxDepth, "…", typeName, IsTruncated: true);

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var propertyCount = element.EnumerateObject().Count();
                var properties = new List<ResultProperty>(Math.Min(propertyCount, 256));
                foreach (var property in element.EnumerateObject())
                {
                    if (!budget.CanTakeNode) break;
                    properties.Add(new(property.Name,
                        CreateJsonElement(property.Value, typeName, depth + 1, budget)));
                }
                return new(
                    SnapshotKind.Json,
                    $"{propertyCount:N0} properties",
                    typeName,
                    properties,
                    IsTruncated: properties.Count < propertyCount,
                    TotalCount: propertyCount);
            }
            case JsonValueKind.Array:
            {
                var itemCount = element.GetArrayLength();
                var items = new List<ResultSnapshot>(Math.Min(itemCount, 256));
                foreach (var item in element.EnumerateArray())
                {
                    if (items.Count >= MaximumItems || !budget.CanTakeNode) break;
                    items.Add(CreateJsonElement(item, typeName, depth + 1, budget));
                }
                return new(
                    SnapshotKind.Json,
                    $"{itemCount:N0} items",
                    typeName,
                    Items: items,
                    IsTruncated: items.Count < itemCount,
                    TotalCount: itemCount);
            }
            case JsonValueKind.String:
            {
                var text = element.GetString() ?? string.Empty;
                return new(
                    SnapshotKind.String,
                    budget.TakeText(text, MaximumStringLength, out var truncated),
                    typeName,
                    IsTruncated: truncated);
            }
            case JsonValueKind.Number:
                return new(SnapshotKind.Number, element.GetRawText(), typeName);
            case JsonValueKind.True:
                return new(SnapshotKind.Boolean, "true", typeName);
            case JsonValueKind.False:
                return new(SnapshotKind.Boolean, "false", typeName);
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return new(SnapshotKind.Null, "null", typeName);
            default:
                return new(SnapshotKind.Json, element.GetRawText(), typeName);
        }
    }

    private static bool IsNumber(Type type) => Type.GetTypeCode(type) is
        TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or
        TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static ReferenceComparer Instance { get; } = new();
        public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }

    private sealed class SnapshotBudget(int remainingNodes, int remainingTextCharacters)
    {
        public bool CanTakeNode => remainingNodes > 0;

        public bool TryTakeNode()
        {
            if (remainingNodes <= 0) return false;
            remainingNodes--;
            return true;
        }

        public string TakeText(string value, int perValueMaximum, out bool truncated)
        {
            var length = Math.Min(value.Length, Math.Min(perValueMaximum, remainingTextCharacters));
            remainingTextCharacters -= length;
            truncated = length < value.Length;
            return length == value.Length ? value : value[..length];
        }
    }
}
