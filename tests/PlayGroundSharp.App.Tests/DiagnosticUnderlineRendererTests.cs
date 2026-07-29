using ICSharpCode.AvalonEdit.Document;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App.Tests;

public sealed class DiagnosticUnderlineRendererTests
{
    [Fact]
    public void CreatesMarkerFromDiagnosticLineAndColumnRange()
    {
        var document = new TextDocument("var value = missing;");
        var diagnostic = new DiagnosticInfo(
            "CS0103",
            DiagnosticLevel.Error,
            "The name 'missing' does not exist.",
            1,
            13,
            1,
            20);

        var marker = Assert.Single(DiagnosticUnderlineRenderer.CreateMarkers(document, [diagnostic]));

        Assert.Equal(12, marker.StartOffset);
        Assert.Equal(7, marker.Length);
        Assert.Equal("missing", document.GetText(marker.StartOffset, marker.Length));
        Assert.Same(diagnostic, marker.Diagnostic);
    }

    [Fact]
    public void EmptyDiagnosticAtEndOfInputUnderlinesThePreviousCharacter()
    {
        var document = new TextDocument("value");
        var diagnostic = new DiagnosticInfo(
            "CS1002",
            DiagnosticLevel.Error,
            "; expected",
            1,
            6,
            1,
            6);

        var marker = Assert.Single(DiagnosticUnderlineRenderer.CreateMarkers(document, [diagnostic]));

        Assert.Equal(4, marker.StartOffset);
        Assert.Equal(1, marker.Length);
        Assert.Equal("e", document.GetText(marker.StartOffset, marker.Length));
    }

    [Fact]
    public void EmptyDocumentAndLineBreakOnlyLocationsDoNotCreateInvalidMarkers()
    {
        var diagnostic = new DiagnosticInfo(
            "CS1002",
            DiagnosticLevel.Error,
            "; expected",
            1,
            1,
            1,
            1);

        Assert.Empty(DiagnosticUnderlineRenderer.CreateMarkers(new TextDocument(), [diagnostic]));
        Assert.Empty(DiagnosticUnderlineRenderer.CreateMarkers(new TextDocument("\r\n"), [diagnostic]));
    }

    [Fact]
    public void ReturnsOverlappingDiagnosticsWithErrorsFirst()
    {
        var document = new TextDocument("missing");
        var warning = new DiagnosticInfo(
            "CS9001",
            DiagnosticLevel.Warning,
            "warning",
            1,
            1,
            1,
            8);
        var error = new DiagnosticInfo(
            "CS0103",
            DiagnosticLevel.Error,
            "error",
            1,
            1,
            1,
            8);
        var markers = DiagnosticUnderlineRenderer.CreateMarkers(document, [warning, error]);

        var diagnostics = DiagnosticUnderlineRenderer.GetDiagnosticsAtOffset(markers, 3);

        Assert.Equal([error, warning], diagnostics);
        Assert.Empty(DiagnosticUnderlineRenderer.GetDiagnosticsAtOffset(markers, 7));
    }
}
