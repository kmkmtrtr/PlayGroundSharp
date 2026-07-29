using PlayGroundSharp.Core;

namespace PlayGroundSharp.Worker.Tests;

public sealed class WorkerEntryPointTests
{
    [Fact]
    public void ParsesTargetFrameworkAndReferenceDirectory()
    {
        var framework = DotNetFrameworkLocator.Discover()
            .FirstOrDefault(static candidate => candidate.ReferenceDirectory is not null);
        if (framework is null) return;

        var parsed = WorkerCommandLine.TryParse(
            [
                "--pipe", "test-pipe",
                "--target-framework", framework.TargetFramework,
                "--framework-reference-directory", framework.ReferenceDirectory!
            ],
            out var configuration);

        Assert.True(parsed);
        Assert.Equal("test-pipe", configuration!.PipeName);
        Assert.Equal(framework.TargetFramework, configuration.TargetFramework);
        Assert.Equal(framework.ReferenceDirectory, configuration.FrameworkReferenceDirectory);
    }

    [Theory]
    [InlineData("--pipe", "test", "--target-framework", "../net10.0")]
    [InlineData("--pipe", "test", "--unknown", "value")]
    [InlineData("--target-framework", "net10.0")]
    public void RejectsInvalidCommandLine(params string[] args)
    {
        Assert.False(WorkerCommandLine.TryParse(args, out _));
    }

    [Fact]
    public async Task HostFailureReturnsExitCodeInsteadOfEscaping()
    {
        var exitCode = await WorkerEntryPoint.RunAsync(string.Empty);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task HostCancellationIsARegularExit()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exitCode = await WorkerEntryPoint.RunAsync($"pgs-cancelled-{Guid.NewGuid():N}", cancellation.Token);

        Assert.Equal(0, exitCode);
    }
}
