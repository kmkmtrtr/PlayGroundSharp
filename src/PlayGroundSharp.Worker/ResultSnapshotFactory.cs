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
        new SnapshotBudget(MaximumNodes, MaximumTextCharacters, default));

    public ResultSnapshot Create(object? value, CancellationToken cancellationToken) => Create(
        value,
        0,
        new HashSet<object>(ReferenceComparer.Instance),
        new SnapshotBudget(MaximumNodes, MaximumTextCharacters, cancellationToken));

    public ResultSnapshot Create(
        object? value,
        int maximumNodes,
        int maximumTextCharacters,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumNodes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTextCharacters);
        return Create(
            value,
            0,
            new HashSet<object>(ReferenceComparer.Instance),
            new SnapshotBudget(maximumNodes, maximumTextCharacters, cancellationToken));
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
        if (value is char character)
        {
            // A lone UTF-16 surrogate cannot round-trip through the JSON IPC payload:
            // System.Text.Json replaces it with U+FFFD. Preserve the exact code unit as
            // ASCII so char[] values containing supplementary Unicode remain inspectable.
            var characterText = char.IsSurrogate(character) ? $"\\u{(int)character:X4}" : character.ToString();
            var display = budget.TakeText(characterText, characterText.Length, out var truncated);
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
        if (value is Half or Int128 or UInt128 or System.Numerics.BigInteger)
        {
            return new(
                SnapshotKind.Number,
                ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
                typeName);
        }
        if (type.IsEnum)
        {
            return new(SnapshotKind.Enum, value.ToString(), typeName);
        }
        if (value is DateTime or DateTimeOffset)
        {
            return new(SnapshotKind.DateTime, ((IFormattable)value).ToString("O", CultureInfo.InvariantCulture), typeName);
        }
        if (value is DateOnly or TimeOnly)
        {
            return new(SnapshotKind.DateTime, ((IFormattable)value).ToString("O", CultureInfo.InvariantCulture), typeName);
        }
        if (value is TimeSpan duration)
        {
            return new(SnapshotKind.String, duration.ToString("c", CultureInfo.InvariantCulture), typeName);
        }
        if (value is Guid guid)
        {
            return new(SnapshotKind.Guid, guid.ToString("D"), typeName);
        }
        if (value is Uri uri)
        {
            var display = budget.TakeText(uri.OriginalString, MaximumStringLength, out var truncated);
            return new(SnapshotKind.String, display, typeName, IsTruncated: truncated);
        }
        if (value is Type reflectedType)
        {
            var display = budget.TakeText(
                reflectedType.FullName ?? reflectedType.Name,
                MaximumStringLength,
                out var truncated);
            return new(SnapshotKind.String, display, typeof(Type).FullName, IsTruncated: truncated);
        }
        if (value is Version version)
        {
            var display = budget.TakeText(version.ToString(), MaximumStringLength, out var truncated);
            return new(SnapshotKind.String, display, typeName, IsTruncated: truncated);
        }
        if (value is System.Text.StringBuilder stringBuilder)
        {
            try
            {
                var sourceLength = stringBuilder.Length;
                var length = Math.Min(
                    sourceLength,
                    Math.Min(MaximumStringLength, budget.RemainingTextCharacters));
                var textValue = stringBuilder.ToString(0, length);
                var display = budget.TakeText(textValue, length, out var budgetTruncated);
                return new(
                    SnapshotKind.String,
                    display,
                    typeName,
                    IsTruncated: budgetTruncated || length < sourceLength);
            }
            catch (Exception error)
            {
                return CreateExceptionSnapshot(error, typeName, budget);
            }
        }
        if (FindGenericBaseType(type, typeof(Lazy<>)) is { } lazyType)
        {
            return CreateLazy(value, lazyType, typeName, depth, path, budget);
        }
        if (value is Task task)
        {
            return CreateTask(task, type, typeName, depth, path, budget);
        }
        if (value is ValueTask valueTask)
        {
            return CreateValueTask(
                typeName,
                valueTask.IsCompleted,
                valueTask.IsCompletedSuccessfully,
                valueTask.IsCanceled,
                valueTask.IsFaulted,
                depth,
                path,
                budget);
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            return CreateGenericValueTask(value, type, typeName, depth, path, budget);
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
            if (value is IDictionary dictionary && HasOnlyStringKeys(dictionary))
            {
                return CreateStringDictionary(dictionary, typeName, depth, path, budget);
            }
            if (value is IEnumerable genericDictionary && TryGetStringDictionaryEntryType(type, out var entryType))
            {
                return CreateGenericStringDictionary(
                    value, genericDictionary, entryType, typeName, depth, path, budget);
            }
            if (value is Array { Rank: > 1 } array)
            {
                var indices = new int[array.Rank];
                return CreateArrayDimension(
                    array, typeName, dimension: 0, depth, path, budget, indices, nodeAlreadyTaken: true);
            }
            if (value is IEnumerable enumerable)
            {
                return CreateSequence(enumerable, typeName, depth, path, budget);
            }

            var properties = new List<ResultProperty>();
            var readableProperties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(static property => property.GetIndexParameters().Length == 0)
                .ToArray();
            var readableFields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
            foreach (var property in readableProperties)
            {
                if (!budget.CanTakeNode) break;
                try
                {
                    if (TryReadPropertyWithoutSubmittedCode(value, property, out var propertyValue))
                    {
                        properties.Add(new(property.Name, Create(propertyValue, depth + 1, path, budget)));
                    }
                    else if (budget.TryTakeNode())
                    {
                        properties.Add(new(property.Name, new(
                            SnapshotKind.String,
                            "getter not evaluated",
                            property.PropertyType.FullName,
                            IsTruncated: true)));
                    }
                }
                catch (Exception error)
                {
                    if (budget.TryTakeNode())
                        properties.Add(new(property.Name,
                            CreateExceptionSnapshot(error, property.PropertyType.FullName, budget)));
                }
            }
            foreach (var field in readableFields)
            {
                if (!budget.CanTakeNode) break;
                try
                {
                    properties.Add(new(field.Name, Create(field.GetValue(value), depth + 1, path, budget)));
                }
                catch (Exception error)
                {
                    if (budget.TryTakeNode())
                        properties.Add(new(field.Name,
                            CreateExceptionSnapshot(error, field.FieldType.FullName, budget)));
                }
            }

            var memberCount = readableProperties.Length + readableFields.Length;

            return new(
                SnapshotKind.Object,
                $"{memberCount} members",
                typeName,
                properties,
                IsTruncated: properties.Count < memberCount,
                TotalCount: memberCount);
        }
        finally
        {
            if (track)
            {
                path.Remove(value);
            }
        }
    }

    private ResultSnapshot CreateTask(
        Task task,
        Type type,
        string typeName,
        int depth,
        HashSet<object> path,
        SnapshotBudget budget)
    {
        if (depth >= MaximumDepth)
            return new(SnapshotKind.MaxDepth, "…", typeName, IsTruncated: true);
        if (!path.Add(task))
            return new(SnapshotKind.Circular, "↻ circular reference", typeName, IsTruncated: true);
        try
        {
            var values = new List<(string Name, object? Value)>
            {
                ("Status", task.Status),
                ("IsCompleted", task.IsCompleted),
                ("IsCompletedSuccessfully", task.IsCompletedSuccessfully),
                ("IsCanceled", task.IsCanceled),
                ("IsFaulted", task.IsFaulted)
            };
            if (task.IsCompletedSuccessfully && FindGenericBaseType(type, typeof(Task<>)) is { } genericTaskType)
                values.Add(("Result", genericTaskType
                    .GetProperty("Result", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)!
                    .GetValue(task)));
            else if (task.IsFaulted)
                values.Add(("Exception", task.Exception));
            return CreateKnownProperties(typeName, task.Status.ToString(), values, depth, path, budget);
        }
        finally
        {
            path.Remove(task);
        }
    }

    private ResultSnapshot CreateLazy(
        object lazy,
        Type lazyType,
        string typeName,
        int depth,
        HashSet<object> path,
        SnapshotBudget budget)
    {
        if (depth >= MaximumDepth)
            return new(SnapshotKind.MaxDepth, "…", typeName, IsTruncated: true);
        if (!path.Add(lazy))
            return new(SnapshotKind.Circular, "↻ circular reference", typeName, IsTruncated: true);
        try
        {
            var isValueCreated = (bool)lazyType
                .GetProperty("IsValueCreated", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)!
                .GetValue(lazy)!;
            var values = new List<(string Name, object? Value)>
            {
                ("IsValueCreated", isValueCreated)
            };
            if (isValueCreated)
                values.Add(("Value", lazyType
                    .GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)!
                    .GetValue(lazy)));
            return CreateKnownProperties(
                typeName,
                isValueCreated ? "ValueCreated" : "NotCreated",
                values,
                depth,
                path,
                budget);
        }
        finally
        {
            path.Remove(lazy);
        }
    }

    private ResultSnapshot CreateGenericValueTask(
        object value,
        Type type,
        string typeName,
        int depth,
        HashSet<object> path,
        SnapshotBudget budget)
    {
        bool Read(string propertyName) => (bool)type.GetProperty(propertyName)!.GetValue(value)!;
        return CreateValueTask(
            typeName,
            Read("IsCompleted"),
            Read("IsCompletedSuccessfully"),
            Read("IsCanceled"),
            Read("IsFaulted"),
            depth,
            path,
            budget);
    }

    private ResultSnapshot CreateValueTask(
        string typeName,
        bool isCompleted,
        bool isCompletedSuccessfully,
        bool isCanceled,
        bool isFaulted,
        int depth,
        HashSet<object> path,
        SnapshotBudget budget)
    {
        if (depth >= MaximumDepth)
            return new(SnapshotKind.MaxDepth, "…", typeName, IsTruncated: true);
        var status = isCompletedSuccessfully
            ? "RanToCompletion"
            : isCanceled
                ? "Canceled"
                : isFaulted
                    ? "Faulted"
                    : isCompleted ? "Completed" : "Pending";
        return CreateKnownProperties(
            typeName,
            status,
            [
                ("Status", status),
                ("IsCompleted", isCompleted),
                ("IsCompletedSuccessfully", isCompletedSuccessfully),
                ("IsCanceled", isCanceled),
                ("IsFaulted", isFaulted)
            ],
            depth,
            path,
            budget);
    }

    private ResultSnapshot CreateKnownProperties(
        string typeName,
        string display,
        IReadOnlyList<(string Name, object? Value)> values,
        int depth,
        HashSet<object> path,
        SnapshotBudget budget)
    {
        var properties = new List<ResultProperty>(values.Count);
        foreach (var (name, value) in values)
        {
            if (!budget.CanTakeNode) break;
            properties.Add(new(name, Create(value, depth + 1, path, budget)));
        }
        return new(
            SnapshotKind.Object,
            display,
            typeName,
            Properties: properties,
            IsTruncated: properties.Count < values.Count,
            TotalCount: values.Count);
    }

    private static Type? FindGenericBaseType(Type type, Type genericTypeDefinition)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
            if (current.IsGenericType && current.GetGenericTypeDefinition() == genericTypeDefinition) return current;
        return null;
    }

    private static bool TryReadPropertyWithoutSubmittedCode(
        object value,
        PropertyInfo property,
        out object? propertyValue)
    {
        var declaringType = property.DeclaringType;
        if (declaringType is null || !IsSubmittedCodeType(declaringType))
        {
            propertyValue = property.GetValue(value);
            return true;
        }

        const BindingFlags backingFieldFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        var backingField = declaringType.GetField(
                               $"<{property.Name}>k__BackingField",
                               backingFieldFlags) ??
                           declaringType.GetField(
                               $"<{property.Name}>i__Field",
                               backingFieldFlags);
        if (backingField?.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) == true)
        {
            propertyValue = backingField.GetValue(value);
            return true;
        }

        propertyValue = null;
        return false;
    }

    private static bool IsSubmittedCodeType(Type type)
    {
        if (type.Assembly.IsDynamic) return true;
        try
        {
            return string.IsNullOrEmpty(type.Assembly.Location);
        }
        catch (NotSupportedException)
        {
            return true;
        }
    }

    private ResultSnapshot CreateStringDictionary(
        IDictionary dictionary,
        string typeName,
        int depth,
        HashSet<object> path,
        SnapshotBudget budget)
    {
        var count = TryGetCollectionCount(dictionary);
        var properties = new List<ResultProperty>(Math.Min(count ?? 0, MaximumItems));
        var enumerationFailed = false;
        var reachedEnd = true;
        try
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (properties.Count >= MaximumItems || !budget.CanTakeNode)
                {
                    reachedEnd = false;
                    break;
                }
                properties.Add(new((string)entry.Key, Create(entry.Value, depth + 1, path, budget)));
            }
        }
        catch (Exception error)
        {
            enumerationFailed = true;
            if (budget.TryTakeNode())
                properties.Add(new("…", CreateExceptionSnapshot(error, null, budget)));
        }

        var snapshotCount = reachedEnd && !enumerationFailed ? properties.Count : count;

        return new(
            SnapshotKind.Object,
            snapshotCount is { } knownCount ? $"{knownCount:N0} entries" : $"{properties.Count:N0} captured entries",
            typeName,
            Properties: properties,
            IsTruncated: enumerationFailed || (snapshotCount is { } total ? properties.Count < total : !reachedEnd),
            TotalCount: snapshotCount);
    }

    private static bool HasOnlyStringKeys(IDictionary dictionary)
    {
        try
        {
            if (dictionary.GetType().GetInterfaces().Any(static contract =>
                    contract.IsGenericType &&
                    contract.GetGenericTypeDefinition() == typeof(IDictionary<,>) &&
                    contract.GetGenericArguments()[0] == typeof(string)))
                return true;

            var inspected = 0;
            foreach (var key in dictionary.Keys)
            {
                if (key is not string) return false;
                if (++inspected > MaximumItems) return false;
            }
            return inspected > 0;
        }
        catch
        {
            return false;
        }
    }

    private ResultSnapshot CreateGenericStringDictionary(
        object value,
        IEnumerable dictionary,
        Type entryType,
        string typeName,
        int depth,
        HashSet<object> path,
        SnapshotBudget budget)
    {
        var count = TryGetGenericDictionaryCount(value, entryType);
        var properties = new List<ResultProperty>(Math.Min(count ?? 0, MaximumItems));
        var keyProperty = entryType.GetProperty("Key")!;
        var valueProperty = entryType.GetProperty("Value")!;
        var reachedEnd = false;
        var enumerationFailed = false;
        IEnumerator? enumerator = null;
        try
        {
            enumerator = dictionary.GetEnumerator();
            while (properties.Count < MaximumItems && budget.CanTakeNode)
            {
                if (!enumerator.MoveNext())
                {
                    reachedEnd = true;
                    break;
                }
                var entry = enumerator.Current;
                var key = (string?)keyProperty.GetValue(entry) ?? string.Empty;
                properties.Add(new(key, Create(valueProperty.GetValue(entry), depth + 1, path, budget)));
            }
        }
        catch (Exception error)
        {
            enumerationFailed = true;
            if (budget.TryTakeNode())
                properties.Add(new("…", CreateExceptionSnapshot(error, null, budget)));
        }
        finally
        {
            try
            {
                (enumerator as IDisposable)?.Dispose();
            }
            catch
            {
                // A broken dictionary enumerator must not invalidate the submission.
            }
        }

        var snapshotCount = reachedEnd && !enumerationFailed ? properties.Count : count;

        return new(
            SnapshotKind.Object,
            snapshotCount is { } knownCount ? $"{knownCount:N0} entries" : $"{properties.Count:N0} captured entries",
            typeName,
            Properties: properties,
            IsTruncated: enumerationFailed ||
                         (snapshotCount is { } total ? properties.Count < total : !reachedEnd),
            TotalCount: snapshotCount);
    }

    private static bool TryGetStringDictionaryEntryType(Type type, out Type entryType)
    {
        foreach (var contract in type.GetInterfaces())
        {
            if (!contract.IsGenericType) continue;
            var definition = contract.GetGenericTypeDefinition();
            if (definition is not null &&
                definition != typeof(IDictionary<,>) && definition != typeof(IReadOnlyDictionary<,>)) continue;
            var arguments = contract.GetGenericArguments();
            if (arguments[0] != typeof(string)) continue;
            entryType = typeof(KeyValuePair<,>).MakeGenericType(arguments);
            return true;
        }
        entryType = null!;
        return false;
    }

    private static int? TryGetGenericDictionaryCount(object value, Type entryType)
    {
        foreach (var definition in new[] { typeof(ICollection<>), typeof(IReadOnlyCollection<>) })
        {
            var contract = definition.MakeGenericType(entryType);
            if (!contract.IsInstanceOfType(value)) continue;
            try
            {
                return (int?)contract.GetProperty("Count")?.GetValue(value);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    private ResultSnapshot CreateArrayDimension(
        Array array,
        string typeName,
        int dimension,
        int depth,
        HashSet<object> path,
        SnapshotBudget budget,
        int[] indices,
        bool nodeAlreadyTaken = false)
    {
        if (!nodeAlreadyTaken && !budget.TryTakeNode())
            return new(SnapshotKind.MaxDepth, "… snapshot limit reached", typeName, IsTruncated: true);
        if (depth >= MaximumDepth)
            return new(SnapshotKind.MaxDepth, "…", typeName, IsTruncated: true);

        var length = array.GetLength(dimension);
        var lowerBound = array.GetLowerBound(dimension);
        var items = new List<ResultSnapshot>(Math.Min(length, MaximumItems));
        while (items.Count < length && items.Count < MaximumItems && budget.CanTakeNode)
        {
            indices[dimension] = lowerBound + items.Count;
            if (dimension + 1 < array.Rank)
            {
                items.Add(CreateArrayDimension(
                    array, typeName, dimension + 1, depth + 1, path, budget, indices));
                continue;
            }

            try
            {
                items.Add(Create(array.GetValue(indices), depth + 1, path, budget));
            }
            catch (Exception error)
            {
                if (budget.TryTakeNode()) items.Add(CreateExceptionSnapshot(error, null, budget));
                break;
            }
        }

        return new(
            SnapshotKind.Sequence,
            $"{length:N0} items",
            typeName,
            Items: items,
            IsTruncated: items.Count < length,
            TotalCount: length);
    }

    private ResultSnapshot CreateSequence(
        IEnumerable enumerable,
        string typeName,
        int depth,
        HashSet<object> path,
        SnapshotBudget budget)
    {
        var items = new List<ResultSnapshot>();
        var totalCount = TryGetCollectionCount(enumerable);
        var sourceHasMoreItems = enumerable is IBoundedSequenceResult { HasMoreItems: true };
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
            var snapshotCount = reachedEnd && !enumerationFailed && !sourceHasMoreItems
                ? items.Count
                : totalCount;
            var truncated = snapshotCount is { } reliableCount
                ? sourceHasMoreItems || enumerationFailed || items.Count < reliableCount
                : sourceHasMoreItems || enumerationFailed || !reachedEnd;
            return new(
                SnapshotKind.Sequence,
                snapshotCount is { } knownCount ? $"{knownCount:N0} items" : $"{items.Count:N0} captured items",
                typeName,
                Items: items,
                IsTruncated: truncated,
                TotalCount: snapshotCount);
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

    private static int? TryGetCollectionCount(IEnumerable enumerable)
    {
        if (enumerable is not ICollection collection) return null;
        try
        {
            var count = collection.Count;
            return count >= 0 ? count : null;
        }
        catch
        {
            return null;
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

    private sealed class SnapshotBudget(
        int remainingNodes,
        int remainingTextCharacters,
        CancellationToken cancellationToken)
    {
        public bool CanTakeNode => remainingNodes > 0;
        public int RemainingTextCharacters => remainingTextCharacters;

        public bool TryTakeNode()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remainingNodes <= 0) return false;
            remainingNodes--;
            return true;
        }

        public string TakeText(string value, int perValueMaximum, out bool truncated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(value.Length, Math.Min(perValueMaximum, remainingTextCharacters));
            remainingTextCharacters -= length;
            truncated = length < value.Length;
            return length == value.Length ? value : value[..length];
        }
    }
}
