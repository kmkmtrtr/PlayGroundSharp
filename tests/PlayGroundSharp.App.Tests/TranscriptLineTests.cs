namespace PlayGroundSharp.App.Tests;

public sealed class TranscriptLineTests
{
    [Theory]
    [InlineData("diagnostic details")]
    [InlineData("system notice")]
    public void PlainTranscriptRowsCanBeCopied(string text)
    {
        var line = text.StartsWith("diagnostic", StringComparison.Ordinal)
            ? TranscriptLine.Diagnostic(text)
            : TranscriptLine.System(text);

        Assert.True(line.IsCopyable);
        Assert.Equal(text, line.CopyText);
    }

    [Fact]
    public void UnknownTruncatedSequenceCopyExplainsThatMoreItemsExist()
    {
        var snapshot = new PlayGroundSharp.Core.ResultSnapshot(
            PlayGroundSharp.Core.SnapshotKind.Sequence,
            "2 captured items",
            "Sample",
            Items:
            [
                new(PlayGroundSharp.Core.SnapshotKind.Number, "1", "System.Int32"),
                new(PlayGroundSharp.Core.SnapshotKind.Number, "2", "System.Int32")
            ],
            IsTruncated: true);

        var line = TranscriptLine.Output(1, SnapshotTextFormatter.FormatCompact(snapshot), snapshot);

        Assert.Contains("more items not captured", line.CopyText, StringComparison.Ordinal);
    }
}
