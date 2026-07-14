namespace PlayGroundSharp.Core;

public enum DiagnosticLevel { Info, Warning, Error }

/// <summary>A serializable compiler diagnostic.</summary>
public sealed record DiagnosticInfo(
    string Id,
    DiagnosticLevel Level,
    string Message,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

public enum SnapshotKind
{
    Null, Number, Boolean, String, Enum, DateTime, Guid, Json, Object, Sequence, Exception, Circular, MaxDepth
}

/// <summary>A bounded, process-neutral representation of an arbitrary result.</summary>
public sealed record ResultSnapshot(
    SnapshotKind Kind,
    string? Display,
    string? TypeName,
    IReadOnlyList<ResultProperty>? Properties = null,
    IReadOnlyList<ResultSnapshot>? Items = null,
    bool IsTruncated = false,
    int? TotalCount = null);

public sealed record ResultProperty(string Name, ResultSnapshot Value);

/// <summary>A process-neutral view of a variable retained by a script session.</summary>
public sealed record VariableInfo(string Name, string TypeName, ResultSnapshot Value, bool IsReadOnly);

/// <summary>A process-neutral exception representation.</summary>
public sealed record ExceptionInfo(string TypeName, string Message, string? StackTrace, ExceptionInfo? InnerException = null);

/// <summary>Shared state used to keep execution and language services aligned.</summary>
public sealed record SessionContext(
    IReadOnlyList<string> Submissions,
    IReadOnlyList<string> Imports,
    IReadOnlyList<string> ReferencePaths)
{
    public static IReadOnlyList<string> DefaultImports { get; } =
    [
        "System", "System.Collections", "System.Collections.Generic", "System.Linq",
        "System.Threading", "System.Threading.Tasks", "System.Text", "System.Text.Json", "System.Text.Json.Nodes"
    ];

    public static SessionContext Empty { get; } = new([], DefaultImports, []);
}
