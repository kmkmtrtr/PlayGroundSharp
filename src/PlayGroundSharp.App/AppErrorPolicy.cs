namespace PlayGroundSharp.App;

internal static class AppErrorPolicy
{
    public static bool CanContinue(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(error);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            if (current is OutOfMemoryException or StackOverflowException or AccessViolationException)
                return false;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
        return true;
    }
}
