using System.IO.Pipes;
using PlayGroundSharp.Core;
using PlayGroundSharp.Worker;

namespace PlayGroundSharp.Worker.Tests;

public sealed class WorkerHostTests
{
    [Fact]
    public async Task PackageRestoreFinishesAllEventsBeforeAllowingTheNextWorkspaceChange()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipeName = $"pgs-package-sequence-{Guid.NewGuid():N}";
        var host = new WorkerHost(
            new WorkerConfiguration(pipeName, $"net{Environment.Version.Major}.0", null),
            new ImmediatePackageRestoreService());
        var hostTask = host.RunAsync(timeout.Token);

        await using (var pipe = new NamedPipeClientStream(
                         ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await pipe.ConnectAsync(timeout.Token);
            await using var transport = new PipeTransport(pipe);
            var packageId = Guid.NewGuid();
            await transport.WriteAsync(PipeEnvelope.Create(
                MessageKinds.AddPackage,
                packageId,
                new AddPackageRequest("Example.Package", "1.2.3")), timeout.Token);

            var packageAdded = await transport.ReadAsync(timeout.Token);
            var packageCompleted = await transport.ReadAsync(timeout.Token);

            Assert.Equal(packageId, packageAdded?.CorrelationId);
            Assert.Equal(MessageKinds.PackageAdded, packageAdded?.Kind);
            Assert.Equal(packageId, packageCompleted?.CorrelationId);
            Assert.Equal(MessageKinds.SessionChanged, packageCompleted?.Kind);

            var usingId = Guid.NewGuid();
            await transport.WriteAsync(PipeEnvelope.Create(
                MessageKinds.AddUsing,
                usingId,
                new AddUsingRequest("System.Net")), timeout.Token);
            var nextChange = await transport.ReadAsync(timeout.Token);

            Assert.Equal(usingId, nextChange?.CorrelationId);
            Assert.Equal(MessageKinds.SessionChanged, nextChange?.Kind);
        }

        await hostTask.WaitAsync(timeout.Token);
    }

    private sealed class ImmediatePackageRestoreService : IPackageRestoreService
    {
        public Task<PackageRestoreResult> RestoreAsync(
            string packageId,
            string? version = null,
            string? source = null,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PackageRestoreResult>(new(packageId, version ?? "1.0.0", []));
    }
}
