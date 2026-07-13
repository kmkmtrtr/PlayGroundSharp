using System.Text.Json;

namespace PlayGroundSharp.Core;

/// <summary>Defines compatibility information for the named-pipe protocol.</summary>
public static class ProtocolConstants
{
    public const int Version = 1;
}

/// <summary>A serialized, versioned message exchanged between App and Worker.</summary>
public sealed record PipeEnvelope(int Version, string Kind, Guid CorrelationId, JsonElement Payload)
{
    public static PipeEnvelope Create<T>(string kind, Guid correlationId, T payload) =>
        new(ProtocolConstants.Version, kind, correlationId, JsonSerializer.SerializeToElement(payload, ProtocolJson.Options));

    public T ReadPayload<T>() => Payload.Deserialize<T>(ProtocolJson.Options)
        ?? throw new InvalidDataException($"Message '{Kind}' has an empty payload.");
}

/// <summary>JSON defaults shared by both IPC endpoints.</summary>
public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}

/// <summary>Message names used on the wire.</summary>
public static class MessageKinds
{
    public const string Execute = "execute";
    public const string Cancel = "cancel";
    public const string Reset = "reset";
    public const string AddReference = "reference.add";
    public const string AddUsing = "using.add";
    public const string AddPackage = "package.add";
    public const string Started = "execution.started";
    public const string ConsoleOut = "console.out";
    public const string ConsoleError = "console.error";
    public const string Diagnostics = "diagnostics";
    public const string Result = "result";
    public const string RuntimeError = "runtime.error";
    public const string Variables = "session.variables";
    public const string Completed = "execution.completed";
    public const string Cancelled = "execution.cancelled";
    public const string SessionChanged = "session.changed";
    public const string PackageProgress = "package.progress";
    public const string PackageAdded = "package.added";
    public const string Error = "worker.error";
}

public sealed record ExecuteRequest(int SubmissionIndex, string Code);
public sealed record CancelRequest(Guid ExecutionId);
public sealed record ResetRequest;
public sealed record AddReferenceRequest(string Path);
public sealed record AddUsingRequest(string Namespace);
public sealed record AddPackageRequest(string PackageId, string? Version = null, string? Source = null);
public sealed record ExecutionStartedEvent(int SubmissionIndex);
public sealed record ConsoleEvent(string Text);
public sealed record DiagnosticsEvent(IReadOnlyList<DiagnosticInfo> Diagnostics);
public sealed record ResultEvent(int SubmissionIndex, ResultSnapshot Snapshot);
public sealed record RuntimeErrorEvent(ExceptionInfo Exception);
public sealed record VariablesEvent(IReadOnlyList<VariableInfo> Variables);
public sealed record ExecutionCompletedEvent(int SubmissionIndex, bool StateAccepted, long WorkerMemoryBytes);
public sealed record CancelledEvent(bool Cooperative);
public sealed record SessionChangedEvent(IReadOnlyList<string> References, IReadOnlyList<string> Usings);
public sealed record PackageProgressEvent(string Message);
public sealed record PackageAddedEvent(string PackageId, string Version, IReadOnlyList<string> AssemblyPaths);
public sealed record WorkerErrorEvent(string Message);
