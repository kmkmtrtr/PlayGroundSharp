using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

/// <summary>Starts, monitors and communicates with the isolated execution Worker.</summary>
public sealed class WorkerClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> pending = new();
    private Process? process;
    private NamedPipeClientStream? pipe;
    private PipeTransport? transport;
    private CancellationTokenSource? lifetime;
    private Task? readLoop;
    private bool isStopping;

    public event Action<PipeEnvelope>? EventReceived;
    public event Action<string>? Disconnected;
    public bool IsConnected => pipe?.IsConnected == true && process is { HasExited: false };

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync().ConfigureAwait(false);
        var pipeName = $"PlayGroundSharp-{Guid.NewGuid():N}";
        var workerDirectory = Path.Combine(AppContext.BaseDirectory, "Worker");
        var executable = Path.Combine(workerDirectory, "PlayGroundSharp.Worker.exe");
        var dll = Path.Combine(workerDirectory, "PlayGroundSharp.Worker.dll");
        var startInfo = File.Exists(executable)
            ? new ProcessStartInfo(executable, $"--pipe {pipeName}")
            : new ProcessStartInfo("dotnet", $"\"{dll}\" --pipe {pipeName}");
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.WorkingDirectory = workerDirectory;
        process = Process.Start(startInfo) ?? throw new InvalidOperationException("Worker process could not be started.");
        process.EnableRaisingEvents = true;
        var startedProcess = process;
        process.Exited += (_, _) =>
        {
            if (!isStopping) Disconnected?.Invoke($"Worker exited with code {startedProcess.ExitCode}.");
        };

        pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
        transport = new PipeTransport(pipe);
        lifetime = new CancellationTokenSource();
        readLoop = ReadLoopAsync(lifetime.Token);
    }

    public Task ExecuteAsync(int index, string code, CancellationToken cancellationToken = default) =>
        SendAndWaitAsync(MessageKinds.Execute, new ExecuteRequest(index, code), cancellationToken);

    public Task ResetAsync(CancellationToken cancellationToken = default) =>
        SendAndWaitAsync(MessageKinds.Reset, new ResetRequest(), cancellationToken);

    public Task AddReferenceAsync(string path, CancellationToken cancellationToken = default) =>
        SendAndWaitAsync(MessageKinds.AddReference, new AddReferenceRequest(path), cancellationToken);

    public Task AddUsingAsync(string @namespace, CancellationToken cancellationToken = default) =>
        SendAndWaitAsync(MessageKinds.AddUsing, new AddUsingRequest(@namespace), cancellationToken);

    public Task AddPackageAsync(string packageId, string? version, CancellationToken cancellationToken = default) =>
        SendAndWaitAsync(MessageKinds.AddPackage, new AddPackageRequest(packageId, version), cancellationToken);

    public Task SearchPackagesAsync(string query, bool includePrerelease, int take = 20, CancellationToken cancellationToken = default) =>
        SendAndWaitAsync(MessageKinds.SearchPackages, new SearchPackagesRequest(query, includePrerelease, take), cancellationToken);

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var correlationId = Guid.NewGuid();
        await transport!.WriteAsync(PipeEnvelope.Create(MessageKinds.Cancel, correlationId, new CancelRequest(correlationId)), cancellationToken).ConfigureAwait(false);
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync().ConfigureAwait(false);
        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAndWaitAsync<T>(string kind, T payload, CancellationToken cancellationToken)
    {
        EnsureConnected();
        var correlationId = Guid.NewGuid();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[correlationId] = completion;
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try
        {
            await transport!.WriteAsync(PipeEnvelope.Create(kind, correlationId, payload), cancellationToken).ConfigureAwait(false);
            await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            pending.TryRemove(correlationId, out _);
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var envelope = await transport!.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (envelope is null) break;
                EventReceived?.Invoke(envelope);
                if (envelope.Kind is MessageKinds.Completed or MessageKinds.Cancelled or MessageKinds.SessionChanged or
                    MessageKinds.PackageSearchResults or MessageKinds.Error)
                {
                    if (pending.TryGetValue(envelope.CorrelationId, out var completion))
                    {
                        if (envelope.Kind == MessageKinds.Error)
                            completion.TrySetException(new InvalidOperationException(envelope.ReadPayload<WorkerErrorEvent>().Message));
                        else
                            completion.TrySetResult();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Disconnected?.Invoke(error.Message);
        }
        finally
        {
            foreach (var completion in pending.Values)
                completion.TrySetException(new IOException("Worker disconnected."));
        }
    }

    private void EnsureConnected()
    {
        if (!IsConnected || transport is null) throw new InvalidOperationException("Worker is not connected.");
    }

    private async Task StopAsync()
    {
        isStopping = true;
        lifetime?.Cancel();
        if (readLoop is not null)
        {
            try { await readLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
        pipe?.Dispose();
        if (process is { HasExited: false })
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        process?.Dispose();
        process = null;
        pipe = null;
        transport = null;
        readLoop = null;
        lifetime?.Dispose();
        lifetime = null;
        isStopping = false;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
