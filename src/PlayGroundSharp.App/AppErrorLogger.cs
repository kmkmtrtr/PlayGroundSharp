using System.IO;
using System.Text;

namespace PlayGroundSharp.App;

internal sealed record AppErrorLogResult(string? Path, string? Failure)
{
    public bool Succeeded => Path is not null;
}

/// <summary>Writes diagnostic information without allowing logging failures to crash the app.</summary>
internal sealed class AppErrorLogger
{
    private const long MaximumLogFileBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private readonly object gate = new();
    private readonly string directory;
    private readonly Func<DateTimeOffset> getCurrentTime;

    public AppErrorLogger(string directory, Func<DateTimeOffset>? getCurrentTime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.GetFullPath(directory);
        this.getCurrentTime = getCurrentTime ?? (() => DateTimeOffset.Now);
    }

    public static AppErrorLogger Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PlayGroundSharp",
        "Logs"));

    public AppErrorLogResult Write(string source, Exception error, bool isTerminating)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            lock (gate)
            {
                var occurredAt = getCurrentTime();
                Directory.CreateDirectory(directory);
                var path = GetLogPath(occurredAt);
                File.AppendAllText(
                    path,
                    FormatEntry(source, error, isTerminating, occurredAt),
                    Utf8WithoutBom);
                return new(path, null);
            }
        }
        catch (Exception loggingError)
        {
            return new(null, $"{loggingError.GetType().Name}: {loggingError.Message}");
        }
    }

    private string GetLogPath(DateTimeOffset occurredAt)
    {
        var dailyPath = Path.Combine(directory, $"PlayGroundSharp-{occurredAt:yyyyMMdd}.log");
        if (!File.Exists(dailyPath) || new FileInfo(dailyPath).Length < MaximumLogFileBytes)
            return dailyPath;
        return Path.Combine(directory, $"PlayGroundSharp-{occurredAt:yyyyMMdd-HHmmssfff}.log");
    }

    private static string FormatEntry(
        string source,
        Exception error,
        bool isTerminating,
        DateTimeOffset occurredAt)
    {
        var version = typeof(AppErrorLogger).Assembly.GetName().Version?.ToString() ?? "unknown";
        return $"""
            [{occurredAt:O}]
            Source: {source}
            Terminating: {isTerminating}
            ProcessId: {Environment.ProcessId}
            ThreadId: {Environment.CurrentManagedThreadId}
            AppVersion: {version}
            Runtime: {Environment.Version}
            OS: {Environment.OSVersion}
            Exception:
            {error}
            --------------------------------------------------------------------------------

            """;
    }
}
