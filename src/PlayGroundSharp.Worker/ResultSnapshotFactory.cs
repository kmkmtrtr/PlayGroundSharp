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
    public const int MaximumItems = 100;
    public const int MaximumStringLength = 1024 * 1024;

    public ResultSnapshot Create(object? value) => Create(value, 0, new HashSet<object>(ReferenceComparer.Instance));

    public static ExceptionInfo CreateException(Exception exception) => new(
        exception.GetType().FullName ?? exception.GetType().Name,
        exception.Message,
        exception.StackTrace,
        exception.InnerException is null ? null : CreateException(exception.InnerException));

    private ResultSnapshot Create(object? value, int depth, HashSet<object> path)
    {
        if (value is null)
        {
            return new(SnapshotKind.Null, "null", null);
        }

        var type = value.GetType();
        var typeName = type.FullName ?? type.Name;
        if (value is string text)
        {
            var truncated = text.Length > MaximumStringLength;
            return new(SnapshotKind.String, truncated ? text[..MaximumStringLength] : text, typeName, IsTruncated: truncated);
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
            return new(SnapshotKind.Json, JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true }), typeName);
        }
        if (value is JsonNode node)
        {
            return new(SnapshotKind.Json, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), typeName);
        }
        if (value is Exception exception)
        {
            var info = CreateException(exception);
            return new(SnapshotKind.Exception, $"{info.TypeName}: {info.Message}", typeName);
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
                return CreateSequence(enumerable, typeName, depth, path);
            }

            var properties = new List<ResultProperty>();
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                         .Where(static property => property.GetIndexParameters().Length == 0))
            {
                try
                {
                    properties.Add(new(property.Name, Create(property.GetValue(value), depth + 1, path)));
                }
                catch (Exception error) when (error is TargetInvocationException or MethodAccessException)
                {
                    properties.Add(new(property.Name, new(SnapshotKind.Exception, error.InnerException?.Message ?? error.Message, null)));
                }
            }

            return new(SnapshotKind.Object, value.ToString(), typeName, properties);
        }
        finally
        {
            if (track)
            {
                path.Remove(value);
            }
        }
    }

    private ResultSnapshot CreateSequence(IEnumerable enumerable, string typeName, int depth, HashSet<object> path)
    {
        var items = new List<ResultSnapshot>();
        var truncated = false;
        var enumerator = enumerable.GetEnumerator();
        try
        {
            while (items.Count < MaximumItems && enumerator.MoveNext())
            {
                items.Add(Create(enumerator.Current, depth + 1, path));
            }
            if (items.Count == MaximumItems && enumerator.MoveNext())
            {
                truncated = true;
            }
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }

        return new(SnapshotKind.Sequence, $"[{string.Join(", ", items.Select(static item => item.Display))}]", typeName, Items: items, IsTruncated: truncated);
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
}
