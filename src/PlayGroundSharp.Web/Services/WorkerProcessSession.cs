using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.Web.Services;

public sealed class WorkerProcessSession : IAsyncDisposable
{
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly object stateGate = new();
    private readonly List<string> submissions = [];
    private readonly List<string> imports = [.. SessionContext.DefaultImports];
    private readonly List<string> references = [];
    private readonly List<WorkspacePackage> packages = [];
    private readonly string sessionDirectory = Path.Combine(Path.GetTempPath(), "PlayGroundSharp", "web", Guid.NewGuid().ToString("N"));
    private Process? process;
    private NamedPipeClientStream? pipe;
    private PipeTransport? transport;

    public SessionContext Context
    {
        get
        {
            lock (stateGate) return new([.. submissions], [.. imports], [.. references]);
        }
    }

    public IReadOnlyList<WorkspacePackage> Packages
    {
        get
        {
            lock (stateGate) return [.. packages];
        }
    }

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

    public async Task<IReadOnlyList<PipeEnvelope>> RemoveUsingAsync(string @namespace, CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            bool hasSubmissions;
            lock (stateGate) hasSubmissions = submissions.Count > 0;
            return hasSubmissions
                ? await RebuildWithoutUsingCoreAsync(@namespace, cancellationToken)
                : await SendCoreAsync(MessageKinds.RemoveUsing, new RemoveUsingRequest(@namespace),
                    static kind => kind is MessageKinds.SessionChanged or MessageKinds.Error, cancellationToken);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public Task<IReadOnlyList<PipeEnvelope>> SearchPackagesAsync(SearchPackagesRequest request, CancellationToken cancellationToken) =>
        SendAsync(MessageKinds.SearchPackages, request, static kind => kind is MessageKinds.PackageSearchResults or MessageKinds.Error, cancellationToken);

    public Task<IReadOnlyList<PipeEnvelope>> AddPackageAsync(AddPackageRequest request, CancellationToken cancellationToken) =>
        SendAsync(MessageKinds.AddPackage, request, static kind => kind is MessageKinds.SessionChanged or MessageKinds.Error, cancellationToken);

    public Task<IReadOnlyList<PipeEnvelope>> AddReferenceAsync(string path, CancellationToken cancellationToken) =>
        SendAsync(MessageKinds.AddReference, new AddReferenceRequest(path), static kind => kind is MessageKinds.SessionChanged or MessageKinds.Error, cancellationToken);

    public async Task<string> SaveFileAsync(string fileName, Stream source, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(sessionDirectory);
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "upload.bin";
        var path = Path.Combine(sessionDirectory, $"{Guid.NewGuid():N}-{safeName}");
        await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            bufferSize: 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken);
        return path;
    }

    private async Task<IReadOnlyList<PipeEnvelope>> SendAsync<T>(
        string kind,
        T request,
        Func<string, bool> isTerminal,
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            return await SendCoreAsync(kind, request, isTerminal, cancellationToken);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<IReadOnlyList<PipeEnvelope>> SendCoreAsync<T>(
        string kind,
        T request,
        Func<string, bool> isTerminal,
        CancellationToken cancellationToken)
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
            if (isTerminal(envelope.Kind))
            {
                ApplyState(kind, request, events);
                return events;
            }
        }
    }

    private async Task<IReadOnlyList<PipeEnvelope>> RebuildWithoutUsingCoreAsync(
        string @namespace,
        CancellationToken cancellationToken)
    {
        string[] savedReferences;
        string[] customImports;
        lock (stateGate)
        {
            savedReferences = [.. references];
            customImports = [.. imports.Where(import =>
                !import.Equals(@namespace, StringComparison.Ordinal) &&
                !SessionContext.DefaultImports.Contains(import, StringComparer.Ordinal))];
        }

        await StopWorkerAsync();
        await StartAsync(cancellationToken);
        lock (stateGate)
        {
            submissions.Clear();
            imports.Clear();
            imports.AddRange(SessionContext.DefaultImports);
            references.Clear();
        }

        foreach (var reference in savedReferences)
        {
            await SendCoreAsync(MessageKinds.AddReference, new AddReferenceRequest(reference),
                static kind => kind is MessageKinds.SessionChanged or MessageKinds.Error, cancellationToken);
        }
        foreach (var import in customImports)
        {
            await SendCoreAsync(MessageKinds.AddUsing, new AddUsingRequest(import),
                static kind => kind is MessageKinds.SessionChanged or MessageKinds.Error, cancellationToken);
        }
        return await SendCoreAsync(MessageKinds.RemoveUsing, new RemoveUsingRequest(@namespace),
            static kind => kind is MessageKinds.SessionChanged or MessageKinds.Error, cancellationToken);
    }

    private void ApplyState<T>(string requestKind, T request, IReadOnlyList<PipeEnvelope> events)
    {
        lock (stateGate)
        {
            foreach (var envelope in events)
            {
                if (envelope.Kind == MessageKinds.SessionChanged)
                {
                    var context = envelope.ReadPayload<SessionChangedEvent>();
                    imports.Clear();
                    imports.AddRange(context.Usings);
                    references.Clear();
                    references.AddRange(context.References);
                }
                else if (envelope.Kind == MessageKinds.PackageAdded)
                {
                    var package = envelope.ReadPayload<PackageAddedEvent>();
                    packages.RemoveAll(item => item.Id.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase));
                    packages.Add(new(package.PackageId, package.Version));
                }
            }

            if (requestKind == MessageKinds.Execute && request is ExecuteRequest execution &&
                events.FirstOrDefault(item => item.Kind == MessageKinds.Completed) is { } completed &&
                completed.ReadPayload<ExecutionCompletedEvent>().StateAccepted)
            {
                submissions.Add(execution.Code);
            }
            else if (requestKind == MessageKinds.Reset)
            {
                submissions.Clear();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopWorkerAsync();
        operationGate.Dispose();
        if (Directory.Exists(sessionDirectory)) Directory.Delete(sessionDirectory, recursive: true);
    }

    private async Task StopWorkerAsync()
    {
        if (transport is not null) await transport.DisposeAsync();
        pipe?.Dispose();
        transport = null;
        pipe = null;
        await DisposeProcessAsync();
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
