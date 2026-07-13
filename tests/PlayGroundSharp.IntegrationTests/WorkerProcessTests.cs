using System.Diagnostics;
using System.IO.Pipes;
using PlayGroundSharp.Core;
using PlayGroundSharp.Worker;

namespace PlayGroundSharp.IntegrationTests;

public sealed class WorkerProcessTests
{
    [Fact]
    public async Task WorkerProcessExecutesMultipleSubmissionsAndStreamsConsole()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pipeName = $"pgs-integration-{Guid.NewGuid():N}";
        using var process = StartWorker(pipeName);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            await using var transport = new PipeTransport(pipe);

            var first = await ExecuteAsync(transport, 1, "var value = 40; Console.WriteLine(\"hello\"); value + 2", timeout.Token);
            var second = await ExecuteAsync(transport, 2, "value * 2", timeout.Token);

            Assert.Contains(first, static envelope => envelope.Kind == MessageKinds.ConsoleOut && envelope.ReadPayload<ConsoleEvent>().Text.Contains("hello", StringComparison.Ordinal));
            Assert.Equal("42", first.Single(static envelope => envelope.Kind == MessageKinds.Result).ReadPayload<ResultEvent>().Snapshot.Display);
            Assert.Contains(first.Single(static envelope => envelope.Kind == MessageKinds.Variables)
                .ReadPayload<VariablesEvent>().Variables, static variable => variable.Name == "value" && variable.Value.Display == "40");
            Assert.Equal("80", second.Single(static envelope => envelope.Kind == MessageKinds.Result).ReadPayload<ResultEvent>().Snapshot.Display);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task WorkerAbnormalExitCanBeDetectedAndReplacementStarted()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var firstPipe = $"pgs-crash-{Guid.NewGuid():N}";
        using var first = StartWorker(firstPipe);
        await using (var connection = new NamedPipeClientStream(".", firstPipe, PipeDirection.InOut, PipeOptions.Asynchronous))
            await connection.ConnectAsync(timeout.Token);
        first.Kill(entireProcessTree: true);
        await first.WaitForExitAsync(timeout.Token);
        Assert.True(first.HasExited);

        var secondPipe = $"pgs-restart-{Guid.NewGuid():N}";
        using var second = StartWorker(secondPipe);
        try
        {
            await using var connection = new NamedPipeClientStream(".", secondPipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await connection.ConnectAsync(timeout.Token);
            Assert.False(second.HasExited);
        }
        finally
        {
            if (!second.HasExited) second.Kill(entireProcessTree: true);
            await second.WaitForExitAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task InfiniteSubmissionCanBeTerminatedAndWorkerReplaced()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pipeName = $"pgs-loop-{Guid.NewGuid():N}";
        using var looping = StartWorker(pipeName);
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        await using var transport = new PipeTransport(pipe);
        var correlationId = Guid.NewGuid();
        await transport.WriteAsync(PipeEnvelope.Create(MessageKinds.Execute, correlationId, new ExecuteRequest(1, "while (true) { }")), timeout.Token);
        var started = await transport.ReadAsync(timeout.Token);
        Assert.Equal(MessageKinds.Started, started?.Kind);

        looping.Kill(entireProcessTree: true);
        await looping.WaitForExitAsync(timeout.Token);
        var replacementPipe = $"pgs-after-loop-{Guid.NewGuid():N}";
        using var replacement = StartWorker(replacementPipe);
        try
        {
            await using var replacementConnection = new NamedPipeClientStream(".", replacementPipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await replacementConnection.ConnectAsync(timeout.Token);
            Assert.False(replacement.HasExited);
        }
        finally
        {
            if (!replacement.HasExited) replacement.Kill(entireProcessTree: true);
            await replacement.WaitForExitAsync(timeout.Token);
        }
    }

    private static Process StartWorker(string pipeName)
    {
        var workerDll = typeof(WorkerHost).Assembly.Location;
        return Process.Start(new ProcessStartInfo("dotnet", $"\"{workerDll}\" --pipe {pipeName}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(workerDll)!
        }) ?? throw new InvalidOperationException("Worker failed to start.");
    }

    private static async Task<IReadOnlyList<PipeEnvelope>> ExecuteAsync(
        PipeTransport transport, int index, string code, CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid();
        await transport.WriteAsync(PipeEnvelope.Create(MessageKinds.Execute, correlationId, new ExecuteRequest(index, code)), cancellationToken);
        var events = new List<PipeEnvelope>();
        while (true)
        {
            var envelope = await transport.ReadAsync(cancellationToken) ?? throw new EndOfStreamException();
            if (envelope.CorrelationId != correlationId) continue;
            events.Add(envelope);
            if (envelope.Kind is MessageKinds.Completed or MessageKinds.Error) return events;
        }
    }
}
