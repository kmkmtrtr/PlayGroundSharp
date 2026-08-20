using System.Collections;
using System.Reflection;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.Worker;

/// <summary>Awaits synchronous sequences of task-like values and captures each result in completion order.</summary>
internal sealed class AsyncSequenceEvaluator(ResultSnapshotFactory snapshots)
{
    private sealed record AwaitedElement(int SourceIndex, bool HasResult, object? Result, Exception? Exception);

    public async Task<bool> TryEvaluateAsync(
        object value,
        Action<int, ResultSnapshot> resultAvailable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(resultAvailable);
        if (value is not IEnumerable sequence || !HasTaskLikeElementType(value.GetType())) return false;

        var pending = Enumerate(sequence, cancellationToken);
        var remainingNodes = ResultSnapshotFactory.MaximumNodes;
        var remainingTextCharacters = ResultSnapshotFactory.MaximumTextCharacters;
        var limitReported = false;
        await foreach (var completed in Task.WhenEach(pending).WithCancellation(cancellationToken))
        {
            var element = await completed.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!element.HasResult && element.Exception is null) continue;
            if (remainingNodes <= 0 || remainingTextCharacters <= 0)
            {
                if (!limitReported)
                {
                    resultAvailable(element.SourceIndex, new(
                        SnapshotKind.MaxDepth,
                        "… streamed result limit reached",
                        null,
                        IsTruncated: true));
                    limitReported = true;
                }
                continue;
            }

            var snapshot = snapshots.Create(
                element.Exception ?? element.Result,
                remainingNodes,
                remainingTextCharacters,
                cancellationToken);
            var usage = MeasureSnapshot(snapshot);
            remainingNodes -= usage.Nodes;
            remainingTextCharacters -= usage.TextCharacters;
            resultAvailable(element.SourceIndex, snapshot);
        }

        return true;
    }

    private static IReadOnlyList<Task<AwaitedElement>> Enumerate(
        IEnumerable sequence,
        CancellationToken cancellationToken)
    {
        var pending = new List<Task<AwaitedElement>>();
        IEnumerator? enumerator = null;
        try
        {
            enumerator = sequence.GetEnumerator();
            while (pending.Count < ResultSnapshotFactory.MaximumItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool hasNext;
                try
                {
                    hasNext = enumerator.MoveNext();
                }
                catch (Exception error)
                {
                    pending.Add(Task.FromResult(new AwaitedElement(pending.Count, false, null, Unwrap(error))));
                    return pending;
                }
                if (!hasNext) return pending;

                object? current;
                try
                {
                    current = enumerator.Current;
                }
                catch (Exception error)
                {
                    pending.Add(Task.FromResult(new AwaitedElement(pending.Count, false, null, Unwrap(error))));
                    return pending;
                }
                pending.Add(AwaitAsync(pending.Count, current, cancellationToken));
            }

            try
            {
                if (enumerator.MoveNext())
                {
                    pending.Add(Task.FromResult(new AwaitedElement(
                        pending.Count,
                        false,
                        null,
                        new InvalidOperationException(
                            $"Async sequence exceeded the {ResultSnapshotFactory.MaximumItems:N0} item limit."))));
                }
            }
            catch (Exception error)
            {
                pending.Add(Task.FromResult(new AwaitedElement(pending.Count, false, null, Unwrap(error))));
            }
            return pending;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            pending.Add(Task.FromResult(new AwaitedElement(0, false, null, Unwrap(error))));
            return pending;
        }
        finally
        {
            try
            {
                (enumerator as IDisposable)?.Dispose();
            }
            catch
            {
                // A broken enumerator is reported during iteration when possible and must not lose prior results.
            }
        }
    }

    private static async Task<AwaitedElement> AwaitAsync(
        int sourceIndex,
        object? awaitable,
        CancellationToken cancellationToken)
    {
        try
        {
            if (awaitable is null)
                throw new InvalidOperationException("Async sequence contains a null task-like value.");
            if (awaitable is Task task)
            {
                await task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return FindGenericBaseType(task.GetType(), typeof(Task<>)) is { } taskType
                    ? new(sourceIndex, true, taskType.GetProperty(
                        "Result",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)!.GetValue(task), null)
                    : new(sourceIndex, false, null, null);
            }
            if (awaitable is ValueTask valueTask)
            {
                await valueTask.AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
                return new(sourceIndex, false, null, null);
            }

            var type = awaitable.GetType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>))
            {
                var resultTask = (Task)type.GetMethod(nameof(ValueTask<int>.AsTask), Type.EmptyTypes)!
                    .Invoke(awaitable, null)!;
                await resultTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                var taskType = FindGenericBaseType(resultTask.GetType(), typeof(Task<>))
                    ?? throw new InvalidOperationException("ValueTask result task has no result type.");
                return new(sourceIndex, true, taskType.GetProperty(
                    "Result",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)!.GetValue(resultTask), null);
            }

            throw new InvalidOperationException($"Sequence item '{type.FullName}' is not task-like.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new(sourceIndex, false, null, Unwrap(error));
        }
    }

    private static bool HasTaskLikeElementType(Type type) => type
        .GetInterfaces()
        .Append(type)
        .Where(static candidate => candidate.IsGenericType &&
                                   candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        .Select(static candidate => candidate.GetGenericArguments()[0])
        .Any(IsTaskLike);

    private static bool IsTaskLike(Type type) =>
        typeof(Task).IsAssignableFrom(type) ||
        type == typeof(ValueTask) ||
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>);

    private static Type? FindGenericBaseType(Type type, Type genericTypeDefinition)
    {
        for (var current = type; current is not null; current = current.BaseType!)
            if (current.IsGenericType && current.GetGenericTypeDefinition() == genericTypeDefinition)
                return current;
        return null;
    }

    private static Exception Unwrap(Exception error) =>
        error is TargetInvocationException { InnerException: { } inner } ? inner : error;

    private static (int Nodes, int TextCharacters) MeasureSnapshot(ResultSnapshot snapshot)
    {
        var nodes = 1;
        var textCharacters = snapshot.Display?.Length ?? 0;
        if (snapshot.Properties is not null)
            foreach (var property in snapshot.Properties)
            {
                textCharacters += property.Name.Length;
                var child = MeasureSnapshot(property.Value);
                nodes += child.Nodes;
                textCharacters += child.TextCharacters;
            }
        if (snapshot.Items is not null)
            foreach (var item in snapshot.Items)
            {
                var child = MeasureSnapshot(item);
                nodes += child.Nodes;
                textCharacters += child.TextCharacters;
            }
        return (nodes, textCharacters);
    }
}
