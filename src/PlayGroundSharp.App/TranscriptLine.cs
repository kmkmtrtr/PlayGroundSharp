using System.Windows.Media;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

public sealed record TranscriptLine(
    string Prefix,
    string Text,
    Brush PrefixBrush,
    Brush TextBrush,
    string? InputCode = null,
    ResultSnapshot? Snapshot = null,
    string? CopyValue = null,
    IReadOnlyList<ConsoleSnapshotNode>? SnapshotRoots = null,
    string? LocalizationKey = null,
    IReadOnlyList<object?>? LocalizationArguments = null)
{
    private const int MaximumConsolePreviewLength = 8 * 1024;
    public bool IsInspectable => Snapshot is not null;
    public bool IsStructured => SnapshotRoots is not null;
    public bool IsInput => InputCode is not null;
    public bool IsConsole => CopyValue is not null;
    // Diagnostics and system messages are often the values users most need to share.
    // Keep every visible transcript row copyable, not only inputs and results.
    public bool IsCopyable => Text.Length > 0 || InputCode is not null || Snapshot is not null || CopyValue is not null;
    public bool IsJsonCopyable => Snapshot is not null;
    public bool IsSavable => Snapshot is not null || CopyValue is not null;
    public string CopyText => CopyValue ?? (Snapshot is null ? Text : SnapshotTextFormatter.FormatFull(Snapshot));

    public static TranscriptLine Input(int index, string code) => new(">", code, Resource("AccentBrush"), Resource("ForegroundBrush"), code);
    public static TranscriptLine Output(int index, string text, ResultSnapshot snapshot) =>
        new(string.Empty, text, Brushes.Transparent, Resource("ForegroundBrush"), Snapshot: snapshot,
            SnapshotRoots: snapshot.Properties is not null || snapshot.Items is not null
                ? [ConsoleSnapshotNode.CreateRoot(snapshot)]
                : null);
    public static TranscriptLine Console(string text, bool error, string previewLimitedMessage)
    {
        var displaySource = text.TrimEnd('\r', '\n');
        var displayText = displaySource.Length <= MaximumConsolePreviewLength
            ? displaySource
            : displaySource[..MaximumConsolePreviewLength] + Environment.NewLine + previewLimitedMessage;
        return new(error ? "!" : "│", displayText,
            Resource(error ? "ErrorBrush" : "MutedBrush"), Resource(error ? "ErrorBrush" : "MutedBrush"),
            CopyValue: text);
    }
    public static TranscriptLine Diagnostic(string text, bool error = true) => new(error ? "×" : "⚠", text,
        Resource(error ? "ErrorBrush" : "WarningBrush"), Resource(error ? "ErrorBrush" : "WarningBrush"));
    public static TranscriptLine System(string text) => new("·", text, Resource("MutedBrush"), Resource("MutedBrush"));
    public static TranscriptLine LocalizedSystem(
        AppLanguageMode languageMode,
        string localizationKey,
        params object?[] arguments) =>
        new("·", AppLocalization.Text(languageMode, localizationKey, arguments),
            Resource("MutedBrush"), Resource("MutedBrush"),
            LocalizationKey: localizationKey, LocalizationArguments: arguments);

    public static TranscriptLine LocalizedDiagnostic(
        AppLanguageMode languageMode,
        string localizationKey,
        bool error = true,
        params object?[] arguments) =>
        new(error ? "×" : "⚠", AppLocalization.Text(languageMode, localizationKey, arguments),
            Resource(error ? "ErrorBrush" : "WarningBrush"), Resource(error ? "ErrorBrush" : "WarningBrush"),
            LocalizationKey: localizationKey, LocalizationArguments: arguments);

    public TranscriptLine WithCurrentLanguage(AppLanguageMode languageMode) => LocalizationKey is null
        ? this
        : this with { Text = AppLocalization.Text(languageMode, LocalizationKey, [.. LocalizationArguments ?? []]) };

    public TranscriptLine WithCurrentTheme() => Prefix switch
    {
        ">" => this with { PrefixBrush = Resource("AccentBrush"), TextBrush = Resource("ForegroundBrush") },
        "!" or "×" => this with { PrefixBrush = Resource("ErrorBrush"), TextBrush = Resource("ErrorBrush") },
        "⚠" => this with { PrefixBrush = Resource("WarningBrush"), TextBrush = Resource("WarningBrush") },
        "│" or "·" => this with { PrefixBrush = Resource("MutedBrush"), TextBrush = Resource("MutedBrush") },
        _ => this with { TextBrush = Resource("ForegroundBrush") }
    };

    private static Brush Resource(string key) =>
        global::System.Windows.Application.Current?.Resources[key] as Brush ?? Brushes.Gray;
}
