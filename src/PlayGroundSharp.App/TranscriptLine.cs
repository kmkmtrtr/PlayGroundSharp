using System.Windows.Media;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

public sealed record TranscriptLine(
    string Prefix,
    string Text,
    Brush PrefixBrush,
    Brush TextBrush,
    string? InputCode = null,
    ResultSnapshot? Snapshot = null)
{
    public bool IsInspectable => Snapshot is not null;

    public static TranscriptLine Input(int index, string code) => new(">", code, Resource("AccentBrush"), Resource("ForegroundBrush"), code);
    public static TranscriptLine Output(int index, string text, ResultSnapshot snapshot) =>
        new(string.Empty, text, Brushes.Transparent, Resource("ForegroundBrush"), Snapshot: snapshot);
    public static TranscriptLine Console(string text, bool error) => new(error ? "!" : "│", text.TrimEnd(),
        Resource(error ? "ErrorBrush" : "MutedBrush"), Resource(error ? "ErrorBrush" : "MutedBrush"));
    public static TranscriptLine Diagnostic(string text, bool error = true) => new(error ? "×" : "⚠", text,
        Resource(error ? "ErrorBrush" : "WarningBrush"), Resource(error ? "ErrorBrush" : "WarningBrush"));
    public static TranscriptLine System(string text) => new("·", text, Resource("MutedBrush"), Resource("MutedBrush"));

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
