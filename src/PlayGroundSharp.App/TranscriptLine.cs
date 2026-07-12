using System.Windows.Media;

namespace PlayGroundSharp.App;

public sealed record TranscriptLine(string Prefix, string Text, Brush PrefixBrush, Brush TextBrush, string? InputCode = null)
{
    public static TranscriptLine Input(int index, string code) => new(">", code, Brushes.RoyalBlue, Brushes.Black, code);
    public static TranscriptLine Output(int index, string text) => new(string.Empty, text, Brushes.Transparent, Brushes.Black);
    public static TranscriptLine Console(string text, bool error) => new(error ? "!" : "│", text.TrimEnd(), error ? Brushes.Firebrick : Brushes.Gray, error ? Brushes.DarkRed : Brushes.DarkSlateGray);
    public static TranscriptLine Diagnostic(string text, bool error = true) => new(error ? "×" : "⚠", text, error ? Brushes.Firebrick : Brushes.DarkGoldenrod, error ? Brushes.DarkRed : Brushes.SaddleBrown);
    public static TranscriptLine System(string text) => new("·", text, Brushes.Gray, Brushes.DimGray);
}
