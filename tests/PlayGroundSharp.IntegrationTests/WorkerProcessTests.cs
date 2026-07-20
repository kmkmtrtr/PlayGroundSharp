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
    public async Task WorkerExitsCleanlyWhenClientDisconnectsDuringAConsoleWrite()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pipeName = $"pgs-disconnect-{Guid.NewGuid():N}";
        using var process = StartWorker(pipeName);
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        PipeTransport? transport = null;
        try
        {
            await pipe.ConnectAsync(timeout.Token);
            transport = new PipeTransport(pipe);
            var correlationId = Guid.NewGuid();
            await transport.WriteAsync(PipeEnvelope.Create(
                MessageKinds.Execute,
                correlationId,
                new ExecuteRequest(1, "Console.WriteLine(new string('x', 5_000_000)); 42")), timeout.Token);

            var started = await transport.ReadAsync(timeout.Token);
            Assert.Equal(MessageKinds.Started, started?.Kind);

            // Leave the large console event unread so the Worker is likely still flushing it
            // when the client goes away, matching a rapid application shutdown.
            await Task.Delay(1_000, timeout.Token);
            await transport.DisposeAsync();
            pipe.Dispose();

            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (transport is not null) await transport.DisposeAsync();
            pipe.Dispose();
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
    public async Task WorkerProcessRemovesUsingBeforeSessionExecution()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pipeName = $"pgs-using-remove-{Guid.NewGuid():N}";
        using var process = StartWorker(pipeName);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            await using var transport = new PipeTransport(pipe);
            var correlationId = Guid.NewGuid();
            await transport.WriteAsync(PipeEnvelope.Create(
                MessageKinds.RemoveUsing, correlationId, new RemoveUsingRequest("System.Linq")), timeout.Token);

            var changed = await transport.ReadAsync(timeout.Token);
            Assert.Equal(MessageKinds.SessionChanged, changed?.Kind);
            Assert.DoesNotContain("System.Linq", changed!.ReadPayload<SessionChangedEvent>().Usings);

            var execution = await ExecuteAsync(transport, 1, "Enumerable.Range(1, 3).Sum()", timeout.Token);
            Assert.Contains(execution, static envelope => envelope.Kind == MessageKinds.Diagnostics);
            Assert.DoesNotContain(execution, static envelope => envelope.Kind == MessageKinds.Result);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(timeout.Token);
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

    [Fact]
    public async Task WorkerRejectsSessionChangesWhileSubmissionIsRunning()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pipeName = $"pgs-busy-{Guid.NewGuid():N}";
        using var process = StartWorker(pipeName);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            await using var transport = new PipeTransport(pipe);
            var executionId = Guid.NewGuid();
            await transport.WriteAsync(PipeEnvelope.Create(
                MessageKinds.Execute, executionId, new ExecuteRequest(1,
                    "Console.WriteLine(\"waiting\"); await Task.Delay(10_000, ExecutionCancellation)")), timeout.Token);
            Assert.Equal(MessageKinds.Started, (await transport.ReadAsync(timeout.Token))?.Kind);
            PipeEnvelope? output;
            do
            {
                output = await transport.ReadAsync(timeout.Token);
            } while (output?.CorrelationId != executionId || output.Kind != MessageKinds.ConsoleOut);

            var usingId = Guid.NewGuid();
            await transport.WriteAsync(PipeEnvelope.Create(
                MessageKinds.AddUsing, usingId, new AddUsingRequest("System.Net")), timeout.Token);
            var rejection = await transport.ReadAsync(timeout.Token);

            Assert.Equal(usingId, rejection?.CorrelationId);
            Assert.Equal(MessageKinds.Error, rejection?.Kind);
            Assert.Contains("running", rejection!.ReadPayload<WorkerErrorEvent>().Message, StringComparison.OrdinalIgnoreCase);

            await transport.WriteAsync(PipeEnvelope.Create(
                MessageKinds.Cancel, Guid.NewGuid(), new CancelRequest(executionId)), timeout.Token);
            PipeEnvelope? cancelled;
            do
            {
                cancelled = await transport.ReadAsync(timeout.Token);
            } while (cancelled?.CorrelationId != executionId || cancelled.Kind != MessageKinds.Cancelled);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(timeout.Token);
        }
    }

    private static Process StartWorker(string pipeName)
    {
        var publishedHost = Environment.GetEnvironmentVariable("PLAYGROUNDSHARP_WORKER_HOST");
        if (!string.IsNullOrWhiteSpace(publishedHost))
        {
            var startInfo = new ProcessStartInfo(publishedHost)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(publishedHost)!
            };
            startInfo.ArgumentList.Add("--worker");
            startInfo.ArgumentList.Add("--pipe");
            startInfo.ArgumentList.Add(pipeName);
            return Process.Start(startInfo) ?? throw new InvalidOperationException("Published Worker host failed to start.");
        }

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
