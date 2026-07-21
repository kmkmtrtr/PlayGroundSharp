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
}
