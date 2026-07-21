using PlayGroundSharp.Core;

namespace PlayGroundSharp.App.Tests;

public sealed class ExecutionDiagnosticTests
{
    [Fact]
    public async Task TranscriptBoundsLargeExecutionDiagnosticSets()
    {
        await using var viewModel = new MainViewModel();
        var diagnostics = Enumerable.Range(1, 600)
            .Select(index => new DiagnosticInfo(
                "CS0103",
                DiagnosticLevel.Error,
                $"missing{index} does not exist",
                index,
                1,
                index,
                8))
            .ToArray();

        viewModel.ApplyExecutionDiagnostics(diagnostics);

        Assert.Equal(101, viewModel.Transcript.Count);
        Assert.Contains("missing100", viewModel.Transcript[99].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(viewModel.Transcript,
            static line => line.Text.Contains("missing101", StringComparison.Ordinal));
        Assert.Equal("⚠", viewModel.Transcript[^1].Prefix);
        Assert.Contains("600", viewModel.Transcript[^1].Text, StringComparison.Ordinal);
        Assert.Contains("500", viewModel.Transcript[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranscriptUsesWorkerDiagnosticTotalWhenPayloadIsBounded()
    {
        await using var viewModel = new MainViewModel();
        var diagnostics = Enumerable.Range(1, 100)
            .Select(index => new DiagnosticInfo(
                "CS0103",
                DiagnosticLevel.Error,
                $"missing{index} does not exist",
                index,
                1,
                index,
                8))
            .ToArray();

        viewModel.ApplyExecutionDiagnostics(diagnostics, 600);

        Assert.Equal(101, viewModel.Transcript.Count);
        Assert.Contains("600", viewModel.Transcript[^1].Text, StringComparison.Ordinal);
        Assert.Contains("500", viewModel.Transcript[^1].Text, StringComparison.Ordinal);
    }
}
