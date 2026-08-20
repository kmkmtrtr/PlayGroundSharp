using PlayGroundSharp.Core;

namespace PlayGroundSharp.Worker;

/// <summary>Globals exposed to every script submission.</summary>
public sealed class SessionGlobals
{
    public object? Last { get; internal set; }
    public ResultHistory Out { get; } = new();
    public LargeDataAccess Data { get; } = new();
    public CancellationToken ExecutionCancellation { get; internal set; }

    public T RetainResultAs<T>(int index) => Out.Name<T>(index);
    public dynamic? RetainResultAsDynamic(int index) => Out.NameDynamic(index);
    public void ReleaseResult(int index) => Out.Release(index);
}

/// <summary>Retains original result objects by one-based submission index.</summary>
public sealed class ResultHistory
{
    private readonly Dictionary<int, RetainedResult> values = [];

    public object? this[int index] => values.TryGetValue(index, out var result)
        ? result.Value
        : throw new KeyNotFoundException($"Submission {index} has no result.");

    public T Name<T>(int index)
    {
        var result = Get(index);
        var value = (T)result.Value!;
        MarkNamed(index);
        return value;
    }

    public dynamic? NameDynamic(int index)
    {
        var result = Get(index);
        MarkNamed(index);
        return result.Value;
    }

    public void MarkNamed(int index) => Get(index).IsNamed = true;

    public bool Release(int index) => values.Remove(index);

    internal IReadOnlyList<RetainedResult> UnnamedResults =>
        [.. values.Values.Where(static result => !result.IsNamed).OrderBy(static result => result.SubmissionIndex)];

    internal void Set(
        int index,
        object? value,
        string typeExpression,
        bool isNamed = false,
        ResultSnapshot? previewSnapshot = null) =>
        values[index] = new(index, value, typeExpression, previewSnapshot) { IsNamed = isNamed };
    internal void Clear() => values.Clear();

    private RetainedResult Get(int index) => values.TryGetValue(index, out var result)
        ? result
        : throw new KeyNotFoundException($"Submission {index} has no result.");
}

internal sealed class RetainedResult(
    int submissionIndex,
    object? value,
    string typeExpression,
    ResultSnapshot? previewSnapshot)
{
    public int SubmissionIndex { get; } = submissionIndex;
    public object? Value { get; } = value;
    public string TypeExpression { get; } = typeExpression;
    public ResultSnapshot? PreviewSnapshot { get; } = previewSnapshot;
    public bool IsNamed { get; set; }
}
