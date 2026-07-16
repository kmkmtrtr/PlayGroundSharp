using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.Web.Services;

public sealed class WorkerProcessSession : IAsyncDisposable
{
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private Process? process;
    private NamedPipeClientStream? pipe;
    private PipeTransport? transport;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var pipeName = $"PlayGroundSharp-Web-{Guid.NewGuid():N}";
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The web host executable path is unavailable.");
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executablePath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("The web host assembly path is unavailable."));
        }
        startInfo.ArgumentList.Add("--web-worker");
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);

        process = Process.Start(startInfo) ?? throw new InvalidOperationException("The Web Worker process could not be started.");
        pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await pipe.ConnectAsync(timeout.Token);
            transport = new PipeTransport(pipe);
        }
        catch
        {
            await DisposeProcessAsync();
            throw;
        }
    }

    public Task<IReadOnlyList<PipeEnvelope>> ExecuteAsync(ExecuteRequest request, CancellationToken cancellationToken) =>
        SendAsync(MessageKinds.Execute, request, static kind => kind is MessageKinds.Completed or MessageKinds.Cancelled or MessageKinds.Error, cancellationToken);

    public Task<IReadOnlyList<PipeEnvelope>> ResetAsync(CancellationToken cancellationToken) =>
        SendAsync(MessageKinds.Reset, new ResetRequest(), static kind => kind is MessageKinds.SessionChanged or MessageKinds.Error, cancellationToken);

    public Task<IReadOnlyList<PipeEnvelope>> AddUsingAsync(string @namespace, CancellationToken cancellationToken) =>
        SendAsync(MessageKinds.AddUsing, new AddUsingRequest(@namespace), static kind => kind is MessageKinds.SessionChanged or MessageKinds.Error, cancellationToken);

    public Task<IReadOnlyList<PipeEnvelope>> RemoveUsingAsync(string @namespace, CancellationToken cancellationToken) =>
        SendAsync(MessageKinds.RemoveUsing, new RemoveUsingRequest(@namespace), static kind => kind is MessageKinds.SessionChanged or MessageKinds.Error, cancellationToken);

    private async Task<IReadOnlyList<PipeEnvelope>> SendAsync<T>(
        string kind,
        T request,
        Func<string, bool> isTerminal,
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            if (process is null || process.HasExited || transport is null)
                throw new InvalidOperationException("The Worker process is not running.");

            var correlationId = Guid.NewGuid();
            await transport.WriteAsync(PipeEnvelope.Create(kind, correlationId, request), cancellationToken);
            var events = new List<PipeEnvelope>();
            while (true)
            {
                var envelope = await transport.ReadAsync(cancellationToken)
                    ?? throw new EndOfStreamException("The Worker disconnected before completing the request.");
                if (envelope.Version != ProtocolConstants.Version)
                    throw new InvalidDataException($"Worker protocol version {envelope.Version} is not supported.");
                if (envelope.CorrelationId != correlationId)
                    throw new InvalidDataException("The Worker returned an event for another request.");
                events.Add(envelope);
                if (isTerminal(envelope.Kind)) return events;
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (transport is not null) await transport.DisposeAsync();
        pipe?.Dispose();
        transport = null;
        pipe = null;
        await DisposeProcessAsync();
        operationGate.Dispose();
    }

    private async Task DisposeProcessAsync()
    {
        if (process is { HasExited: false })
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        process?.Dispose();
        process = null;
    }
}
