using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

internal sealed class DiagnosticUnderlineRenderer(TextDocument document) : IBackgroundRenderer
{
    private readonly TextSegmentCollection<DiagnosticMarker> markers = new(document);

    // Draw after the text layer so glyphs and the input background cannot cover the squiggle.
    public KnownLayer Layer => KnownLayer.Caret;

    public void UpdateDiagnostics(IReadOnlyList<DiagnosticInfo> diagnostics, TextView textView)
    {
        markers.Clear();
        foreach (var marker in CreateMarkers(document, diagnostics)) markers.Add(marker);
        textView.InvalidateLayer(Layer);
    }

    public void Invalidate(TextView textView) => textView.InvalidateLayer(Layer);

    public IReadOnlyList<DiagnosticInfo> GetDiagnosticsAtOffset(int offset) =>
        GetDiagnosticsAtOffset(markers.FindSegmentsContaining(offset).ToArray(), offset);

    internal static IReadOnlyList<DiagnosticInfo> GetDiagnosticsAtOffset(
        IReadOnlyList<DiagnosticMarker> source,
        int offset) =>
        source
            .Where(marker => offset >= marker.StartOffset && offset < marker.EndOffset)
            .Select(static marker => marker.Diagnostic)
            .OrderByDescending(static diagnostic => diagnostic.Level)
            .ThenBy(static diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .ToArray();

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (markers.Count == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0)
            return;

        var firstVisibleOffset = textView.VisualLines[0].FirstDocumentLine.Offset;
        var lastVisibleLine = textView.VisualLines[^1].LastDocumentLine;
        var lastVisibleOffset = lastVisibleLine.Offset + lastVisibleLine.TotalLength;
        var pens = new Dictionary<DiagnosticLevel, Pen>();

        var visibleMarkers = markers.FindOverlappingSegments(
            firstVisibleOffset,
            Math.Max(0, lastVisibleOffset - firstVisibleOffset));
        foreach (var marker in visibleMarkers.OrderBy(static marker => marker.Diagnostic.Level))
        {
            if (marker.EndOffset <= firstVisibleOffset || marker.StartOffset > lastVisibleOffset)
                continue;

            if (!pens.TryGetValue(marker.Diagnostic.Level, out var pen))
            {
                pen = CreatePen(marker.Diagnostic.Level);
                pens.Add(marker.Diagnostic.Level, pen);
            }

            foreach (var rectangle in BackgroundGeometryBuilder.GetRectsForSegment(textView, marker, false))
                DrawWave(drawingContext, rectangle, pen);
        }
    }

    internal static IReadOnlyList<DiagnosticMarker> CreateMarkers(
        TextDocument document,
        IReadOnlyList<DiagnosticInfo> diagnostics)
    {
        if (document.TextLength == 0 || diagnostics.Count == 0) return [];

        var result = new List<DiagnosticMarker>(diagnostics.Count);
        foreach (var diagnostic in diagnostics)
        {
            var start = GetOffset(document, diagnostic.StartLine, diagnostic.StartColumn);
            var end = GetOffset(document, diagnostic.EndLine, diagnostic.EndColumn);
            if (end < start) end = start;
            if (end == start && !TryExpandEmptyRange(document, start, out start, out end))
                continue;
            result.Add(new(start, end - start, diagnostic));
        }
        return result;
    }

    private static int GetOffset(TextDocument document, int lineNumber, int column)
    {
        var line = document.GetLineByNumber(Math.Clamp(lineNumber, 1, document.LineCount));
        return line.Offset + Math.Clamp(column - 1, 0, line.Length);
    }

    private static bool TryExpandEmptyRange(
        TextDocument document,
        int offset,
        out int start,
        out int end)
    {
        if (offset > 0 && !IsLineBreak(document.GetCharAt(offset - 1)))
        {
            start = offset - 1;
            end = offset;
            return true;
        }
        if (offset < document.TextLength && !IsLineBreak(document.GetCharAt(offset)))
        {
            start = offset;
            end = offset + 1;
            return true;
        }

        start = offset;
        end = offset;
        return false;
    }

    private static bool IsLineBreak(char value) => value is '\r' or '\n';

    private static Pen CreatePen(DiagnosticLevel level)
    {
        var resourceKey = level switch
        {
            DiagnosticLevel.Error => "ErrorBrush",
            DiagnosticLevel.Warning => "WarningBrush",
            _ => "AccentBrush"
        };
        var fallback = level switch
        {
            DiagnosticLevel.Error => Color.FromRgb(241, 76, 76),
            DiagnosticLevel.Warning => Color.FromRgb(204, 167, 0),
            _ => Color.FromRgb(25, 103, 210)
        };
        var brush = Application.Current?.TryFindResource(resourceKey) as Brush ??
                    new SolidColorBrush(fallback);
        var pen = new Pen(brush, 1.6);
        if (pen.CanFreeze) pen.Freeze();
        return pen;
    }

    private static void DrawWave(DrawingContext drawingContext, Rect rectangle, Pen pen)
    {
        if (rectangle.Width <= 0) return;

        const double step = 2.25;
        const double amplitude = 1.4;
        var left = rectangle.Left;
        var right = Math.Max(left + step, rectangle.Right);
        var baseline = rectangle.Bottom - amplitude - 1;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new(left, baseline), false, false);
            var raise = true;
            for (var x = left + step; x < right; x += step)
            {
                context.LineTo(new(x, baseline + (raise ? amplitude : -amplitude)), true, false);
                raise = !raise;
            }
            context.LineTo(new(right, baseline + (raise ? amplitude : -amplitude)), true, false);
        }
        if (geometry.CanFreeze) geometry.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }
}

internal sealed class DiagnosticMarker : TextSegment
{
    public DiagnosticMarker(int offset, int length, DiagnosticInfo diagnostic)
    {
        StartOffset = offset;
        Length = length;
        Diagnostic = diagnostic;
    }

    public DiagnosticInfo Diagnostic { get; }
}
