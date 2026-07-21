using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

public enum WorkerDisconnectionKind { ProcessExited, PipeClosed, TransportError }
public sealed record WorkerDisconnection(WorkerDisconnectionKind Kind, int? ExitCode = null, string? Detail = null);

/// <summary>Starts, monitors and communicates with the isolated execution Worker.</summary>
public sealed class WorkerClient : IAsyncDisposable
{
    private const int MaximumCapturedErrorCharacters = 16 * 1024;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> pending = new();
    private readonly object errorOutputGate = new();
    private readonly StringBuilder errorOutput = new();
    private Process? process;
    private NamedPipeClientStream? pipe;
    private PipeTransport? transport;
    private CancellationTokenSource? lifetime;
    private Task? readLoop;
    private volatile bool isStopping;
    private long connectionGeneration;
    private int disconnectionReported;

    public event Action<PipeEnvelope>? EventReceived;
    public event Action<WorkerDisconnection>? Disconnected;
    public bool IsConnected => pipe?.IsConnected == true && process is { HasExited: false };

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync().ConfigureAwait(false);
        var generation = Interlocked.Increment(ref connectionGeneration);
        Volatile.Write(ref disconnectionReported, 0);
        var pipeName = $"PlayGroundSharp-{Guid.NewGuid():N}";
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The application executable path is unavailable.");
        var startInfo = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("--worker");
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        process = Process.Start(startInfo) ?? throw new InvalidOperationException("Worker process could not be started.");
        try
        {
            lock (errorOutputGate) errorOutput.Clear();
            process.ErrorDataReceived += (_, args) => CaptureErrorOutput(args.Data);
            process.BeginErrorReadLine();
            var startedProcess = process;
            process.Exited += (_, _) =>
            {
                if (isStopping) return;
                int? exitCode = null;
                try
                {
                    exitCode = startedProcess.ExitCode;
                }
                catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
                {
                    // Preserve the disconnection notification even if the exit code raced disposal.
                }
                ReportDisconnected(generation,
                    new(WorkerDisconnectionKind.ProcessExited, exitCode, GetCapturedErrorOutput()));
            };
            process.EnableRaisingEvents = true;

            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
            transport = new PipeTransport(pipe);
            lifetime = new CancellationTokenSource();
            readLoop = ReadLoopAsync(generation, lifetime.Token);
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task ExecuteAsync(int index, string code, CancellationToken cancellationToken = default) =>
        SendAndWaitAsync(MessageKinds.Execute, new ExecuteRequest(index, code), cancellationToken);

    public Task ResetAsync(CancellationToken cancellationToken = default) =>
        SendAndWaitAsync(MessageKinds.Reset, new ResetRequest(), cancellationToken);

    public Task AddReferenceAsync(string path, CancellationToken cancellationToken = default) =>
        SendAndWaitAsync(MessageKinds.AddReference, new AddReferenceRequest(path), cancellationToken);

    public Task AddUsingAsync(string @namespace, CancellationToken cancellationToken = default) =>
        SendAndWaitAsync(MessageKinds.AddUsing, new AddUsingRequest(@namespace), cancellationToken);

    public Task RemoveUsingAsync(string @namespace, CancellationToken cancellationToken = default) =>
        SendAndWaitAsync(MessageKinds.RemoveUsing, new RemoveUsingRequest(@namespace), cancellationToken);

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

    private async Task ReadLoopAsync(long generation, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var envelope = await transport!.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (envelope is null)
                {
                    if (!cancellationToken.IsCancellationRequested)
                        ReportDisconnected(generation, new(WorkerDisconnectionKind.PipeClosed));
                    break;
                }
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
            ReportDisconnected(generation, new(WorkerDisconnectionKind.TransportError, Detail: error.Message));
        }
        finally
        {
            foreach (var completion in pending.Values)
                completion.TrySetException(new IOException("Worker disconnected."));
        }
    }

    private void ReportDisconnected(long generation, WorkerDisconnection disconnection)
    {
        if (isStopping || generation != Volatile.Read(ref connectionGeneration) ||
            Interlocked.Exchange(ref disconnectionReported, 1) != 0) return;
        Disconnected?.Invoke(disconnection);
    }

    private void CaptureErrorOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (errorOutputGate)
        {
            if (errorOutput.Length >= MaximumCapturedErrorCharacters) return;
            if (errorOutput.Length > 0 &&
                errorOutput.Length + Environment.NewLine.Length < MaximumCapturedErrorCharacters)
                errorOutput.AppendLine();
            var remaining = MaximumCapturedErrorCharacters - errorOutput.Length;
            if (remaining > 0) errorOutput.Append(line.AsSpan(0, Math.Min(line.Length, remaining)));
        }
    }

    private string? GetCapturedErrorOutput()
    {
        lock (errorOutputGate)
            return errorOutput.Length == 0 ? null : errorOutput.ToString();
    }

    private void EnsureConnected()
    {
        if (!IsConnected || transport is null) throw new InvalidOperationException("Worker is not connected.");
    }

    private async Task StopAsync()
    {
        isStopping = true;
        Interlocked.Increment(ref connectionGeneration);
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
