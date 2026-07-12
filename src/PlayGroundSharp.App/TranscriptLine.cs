using System.Windows.Media;

namespace PlayGroundSharp.App;

public sealed record TranscriptLine(string Prefix, string Text, Brush PrefixBrush, Brush TextBrush, string? InputCode = null)
{
    public static TranscriptLine Input(int index, string code) => new($"In [{index}] ›", code, Brushes.RoyalBlue, Brushes.Black, code);
    public static TranscriptLine Output(int index, string text) => new($"Out[{index}]", text, Brushes.DarkViolet, Brushes.Black);
    public static TranscriptLine Console(string text, bool error) => new(error ? "stderr" : "stdout", text.TrimEnd(), error ? Brushes.Firebrick : Brushes.DimGray, error ? Brushes.DarkRed : Brushes.DarkSlateGray);
    public static TranscriptLine Diagnostic(string text, bool error = true) => new(error ? "error" : "warning", text, error ? Brushes.Firebrick : Brushes.DarkGoldenrod, error ? Brushes.DarkRed : Brushes.SaddleBrown);
    public static TranscriptLine System(string text) => new("system", text, Brushes.Gray, Brushes.DimGray);
}
