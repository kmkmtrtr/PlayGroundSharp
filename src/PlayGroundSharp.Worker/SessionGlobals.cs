namespace PlayGroundSharp.Worker;

/// <summary>Globals exposed to every script submission.</summary>
public sealed class SessionGlobals
{
    public object? Last { get; internal set; }
    public ResultHistory Out { get; } = new();
}

/// <summary>Retains original result objects by one-based submission index.</summary>
public sealed class ResultHistory
{
    private readonly Dictionary<int, object?> values = [];

    public object? this[int index] => values.TryGetValue(index, out var value)
        ? value
        : throw new KeyNotFoundException($"Submission {index} has no result.");

    internal void Set(int index, object? value) => values[index] = value;
    internal void Clear() => values.Clear();
}
