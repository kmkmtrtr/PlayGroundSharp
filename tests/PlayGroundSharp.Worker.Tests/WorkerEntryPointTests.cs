namespace PlayGroundSharp.Worker.Tests;

public sealed class WorkerEntryPointTests
{
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
