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

    // Framework references used by ScriptOptions.Default plus assemblies exposed by the modeled globals.
    // This deliberately conservative set prevents editor-only dependencies (for example Humanizer used
    // internally by Roslyn) from leaking out of the app host and into session completion.
    public static IReadOnlySet<string> DefaultReferenceAssemblyNames { get; } = new HashSet<string>(
    [
        "System.Private.CoreLib", "System", "System.Collections", "System.Collections.Concurrent", "System.Console",
        "System.Diagnostics", "System.Diagnostics.Debug", "System.Diagnostics.Process", "System.Diagnostics.StackTrace",
        "System.Globalization", "System.IO", "System.IO.FileSystem", "System.IO.FileSystem.Primitives",
        "System.Linq", "System.Reflection", "System.Reflection.Extensions", "System.Reflection.Primitives",
        "System.Runtime", "System.Runtime.Extensions", "System.Runtime.InteropServices", "System.Text", "System.Text.Encoding",
        "System.Text.Encoding.CodePages", "System.Text.Encoding.Extensions", "System.Text.Json",
        "System.Text.RegularExpressions", "System.Threading", "System.Threading.Tasks",
        "System.Threading.Tasks.Parallel", "System.Threading.Thread", "System.ValueTuple",
        "PlayGroundSharp.Core"
    ], StringComparer.OrdinalIgnoreCase);

    public static SessionContext Empty { get; } = new([], DefaultImports, []);
}
