namespace PlayGroundSharp.App.Tests;

public sealed class AppErrorHandlingTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"PlayGroundSharp-error-tests-{Guid.NewGuid():N}");

    [Fact]
    public void WritesDetailedAppendOnlyLogEntries()
    {
        var occurredAt = new DateTimeOffset(2026, 7, 31, 12, 34, 56, TimeSpan.FromHours(9));
        var logger = new AppErrorLogger(directory, () => occurredAt);
        var error = new InvalidOperationException(
            "outer failure",
            new ArgumentException("inner failure"));

        var first = logger.Write("UI Dispatcher", error, isTerminating: false);
        var second = logger.Write("Background task", error, isTerminating: true);

        Assert.True(first.Succeeded);
        Assert.Equal(first.Path, second.Path);
        var log = File.ReadAllText(first.Path!);
        Assert.Contains("[2026-07-31T12:34:56.0000000+09:00]", log, StringComparison.Ordinal);
        Assert.Contains("Source: UI Dispatcher", log, StringComparison.Ordinal);
        Assert.Contains("Source: Background task", log, StringComparison.Ordinal);
        Assert.Contains("Terminating: False", log, StringComparison.Ordinal);
        Assert.Contains("Terminating: True", log, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: outer failure", log, StringComparison.Ordinal);
        Assert.Contains("ArgumentException: inner failure", log, StringComparison.Ordinal);
        Assert.Contains("ProcessId:", log, StringComparison.Ordinal);
        Assert.Contains("ThreadId:", log, StringComparison.Ordinal);
        Assert.Contains("Runtime:", log, StringComparison.Ordinal);
        Assert.Contains("OS:", log, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsLoggingFailureWithoutThrowing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        File.WriteAllText(directory, "not a directory");
        var logger = new AppErrorLogger(directory);

        var result = logger.Write("Test", new InvalidOperationException("failure"), false);

        Assert.False(result.Succeeded);
        Assert.Null(result.Path);
        Assert.False(string.IsNullOrWhiteSpace(result.Failure));
    }

    [Fact]
    public void ContinuesAfterOrdinaryErrorsButNotFatalErrors()
    {
        Assert.True(AppErrorPolicy.CanContinue(new InvalidOperationException("recoverable")));
        Assert.False(AppErrorPolicy.CanContinue(new OutOfMemoryException("fatal")));
        Assert.False(AppErrorPolicy.CanContinue(new AggregateException(
            new InvalidOperationException("outer"),
            new AccessViolationException("fatal inner"))));
    }

    [Fact]
    public void DialogMessageExplainsContinuationAndLogLocation()
    {
        var message = AppErrorDialog.BuildMessage(
            AppLanguageMode.English,
            new InvalidOperationException("sample failure"),
            new AppErrorLogResult(@"C:\Logs\PlayGroundSharp.log", null),
            canContinue: true);

        Assert.Contains("continue", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("InvalidOperationException: sample failure", message, StringComparison.Ordinal);
        Assert.Contains(@"C:\Logs\PlayGroundSharp.log", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DialogMessageReportsWhenLogCouldNotBeWritten()
    {
        var message = AppErrorDialog.BuildMessage(
            AppLanguageMode.Japanese,
            new OutOfMemoryException("sample failure"),
            new AppErrorLogResult(null, "Access denied"),
            canContinue: false);

        Assert.Contains("アプリを終了", message, StringComparison.Ordinal);
        Assert.Contains("ログを保存できませんでした", message, StringComparison.Ordinal);
        Assert.Contains("Access denied", message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (File.Exists(directory)) File.Delete(directory);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
