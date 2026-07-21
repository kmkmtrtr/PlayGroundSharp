using System.Diagnostics;
using System.IO.Pipes;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.Worker;

/// <summary>Serves one App connection and serializes access to a ScriptSession.</summary>
public sealed class WorkerHost(string pipeName)
{
    private readonly ScriptSession session = new();
    private readonly PackageRestoreService packageRestore = new();
    private readonly PackageSearchService packageSearch = new();
    private volatile CancellationTokenSource? operationCancellation;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transport = new PipeTransport(pipe);
        while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
        {
            var envelope = await transport.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (envelope is null) break;
            await HandleAsync(transport, envelope, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(PipeTransport transport, PipeEnvelope envelope, CancellationToken hostToken)
    {
        try
        {
            if (operationCancellation is not null && envelope.Kind is not MessageKinds.Cancel)
                throw new InvalidOperationException("Session changes are unavailable while another operation is running.");
            switch (envelope.Kind)
            {
                case MessageKinds.Execute:
                    StartOperation(transport, envelope, hostToken,
                        token => ExecuteAsync(transport, envelope, hostToken, token));
                    break;
                case MessageKinds.Cancel:
                    CancelCurrentOperation();
                    break;
                case MessageKinds.Reset:
                    session.Reset();
                    await SendContextAsync(transport, envelope.CorrelationId, hostToken).ConfigureAwait(false);
                    break;
                case MessageKinds.AddReference:
                    session.AddReference(envelope.ReadPayload<AddReferenceRequest>().Path);
                    await SendContextAsync(transport, envelope.CorrelationId, hostToken).ConfigureAwait(false);
                    break;
                case MessageKinds.AddUsing:
                    session.AddUsing(envelope.ReadPayload<AddUsingRequest>().Namespace);
                    await SendContextAsync(transport, envelope.CorrelationId, hostToken).ConfigureAwait(false);
                    break;
                case MessageKinds.RemoveUsing:
                    session.RemoveUsing(envelope.ReadPayload<RemoveUsingRequest>().Namespace);
                    await SendContextAsync(transport, envelope.CorrelationId, hostToken).ConfigureAwait(false);
                    break;
                case MessageKinds.AddPackage:
                    StartOperation(transport, envelope, hostToken,
                        token => AddPackageAsync(transport, envelope, token));
                    break;
                case MessageKinds.SearchPackages:
                    StartOperation(transport, envelope, hostToken,
                        token => SearchPackagesAsync(transport, envelope, token));
                    break;
                default:
                    throw new InvalidDataException($"Unknown message kind '{envelope.Kind}'.");
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await transport.WriteAsync(PipeEnvelope.Create(MessageKinds.Error, envelope.CorrelationId,
                new WorkerErrorEvent(error.Message)), hostToken).ConfigureAwait(false);
        }
    }

    private void CancelCurrentOperation()
    {
        try
        {
            operationCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation completed between reading the field and requesting cancellation.
        }
    }

    private void StartOperation(
        PipeTransport transport,
        PipeEnvelope envelope,
        CancellationToken hostToken,
        Func<CancellationToken, Task> operation)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        operationCancellation = linked;
        _ = RunOperationAsync(transport, envelope, hostToken, linked, operation);
    }

    private async Task RunOperationAsync(
        PipeTransport transport,
        PipeEnvelope envelope,
        CancellationToken hostToken,
        CancellationTokenSource cancellation,
        Func<CancellationToken, Task> operation)
    {
        try
        {
            await operation(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ReleaseOperation(cancellation.Token);
            await TryWriteAsync(transport, PipeEnvelope.Create(
                MessageKinds.Cancelled, envelope.CorrelationId, new CancelledEvent(true)), hostToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ReleaseOperation(cancellation.Token);
            await TryWriteAsync(transport, PipeEnvelope.Create(
                MessageKinds.Error, envelope.CorrelationId, new WorkerErrorEvent(error.Message)), hostToken).ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(operationCancellation, cancellation)) operationCancellation = null;
            cancellation.Dispose();
        }
    }

    private void ReleaseOperation(CancellationToken operationToken)
    {
        var current = operationCancellation;
        if (current is not null && current.Token == operationToken)
            Interlocked.CompareExchange(ref operationCancellation, null, current);
    }

    private static async Task TryWriteAsync(
        PipeTransport transport,
        PipeEnvelope envelope,
        CancellationToken cancellationToken)
    {
        try
        {
            await transport.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The App detects a closed transport and replaces this Worker.
        }
    }

    private async Task ExecuteAsync(
        PipeTransport transport,
        PipeEnvelope envelope,
        CancellationToken hostToken,
        CancellationToken operationToken)
    {
        var request = envelope.ReadPayload<ExecuteRequest>();
        await transport.WriteAsync(PipeEnvelope.Create(MessageKinds.Started, envelope.CorrelationId,
            new ExecutionStartedEvent(request.SubmissionIndex)), hostToken).ConfigureAwait(false);
        var result = await session.ExecuteAsync(request.SubmissionIndex, request.Code,
            text => transport.WriteAsync(PipeEnvelope.Create(MessageKinds.ConsoleOut, envelope.CorrelationId, new ConsoleEvent(text)), hostToken).GetAwaiter().GetResult(),
            text => transport.WriteAsync(PipeEnvelope.Create(MessageKinds.ConsoleError, envelope.CorrelationId, new ConsoleEvent(text)), hostToken).GetAwaiter().GetResult(),
            operationToken).ConfigureAwait(false);
        if (result.Diagnostics.Count > 0)
            await transport.WriteAsync(PipeEnvelope.Create(
                MessageKinds.Diagnostics,
                envelope.CorrelationId,
                new DiagnosticsEvent(result.Diagnostics, result.TotalDiagnosticCount)), hostToken).ConfigureAwait(false);
        if (result.Snapshot is not null)
            await transport.WriteAsync(PipeEnvelope.Create(MessageKinds.Result, envelope.CorrelationId, new ResultEvent(request.SubmissionIndex, result.Snapshot)), hostToken).ConfigureAwait(false);
        if (result.Exception is not null)
            await transport.WriteAsync(PipeEnvelope.Create(MessageKinds.RuntimeError, envelope.CorrelationId, new RuntimeErrorEvent(result.Exception)), hostToken).ConfigureAwait(false);
        await transport.WriteAsync(PipeEnvelope.Create(MessageKinds.Variables, envelope.CorrelationId,
            new VariablesEvent(session.GetVariables())), hostToken).ConfigureAwait(false);
        // A terminal event is the client's signal that it may submit the next operation.
        ReleaseOperation(operationToken);
        await transport.WriteAsync(PipeEnvelope.Create(MessageKinds.Completed, envelope.CorrelationId,
            new ExecutionCompletedEvent(request.SubmissionIndex, result.StateAccepted, Process.GetCurrentProcess().WorkingSet64)), hostToken).ConfigureAwait(false);
    }

    private Task SendContextAsync(PipeTransport transport, Guid correlationId, CancellationToken cancellationToken)
    {
        var context = session.Context;
        return transport.WriteAsync(PipeEnvelope.Create(MessageKinds.SessionChanged, correlationId,
            new SessionChangedEvent(context.ReferencePaths, context.Imports)), cancellationToken);
    }

    private async Task AddPackageAsync(PipeTransport transport, PipeEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = envelope.ReadPayload<AddPackageRequest>();
        var restored = await packageRestore.RestoreAsync(request.PackageId, request.Version, request.Source,
            message => transport.WriteAsync(PipeEnvelope.Create(MessageKinds.PackageProgress, envelope.CorrelationId,
                new PackageProgressEvent(message)), cancellationToken).GetAwaiter().GetResult(), cancellationToken).ConfigureAwait(false);
        foreach (var path in restored.AssemblyPaths) session.AddReference(path);
        await SendContextAsync(transport, envelope.CorrelationId, cancellationToken).ConfigureAwait(false);
        ReleaseOperation(cancellationToken);
        await transport.WriteAsync(PipeEnvelope.Create(MessageKinds.PackageAdded, envelope.CorrelationId,
            new PackageAddedEvent(restored.PackageId, restored.Version, restored.AssemblyPaths)), cancellationToken).ConfigureAwait(false);
    }

    private async Task SearchPackagesAsync(PipeTransport transport, PipeEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = envelope.ReadPayload<SearchPackagesRequest>();
        var results = await packageSearch.SearchAsync(
            request.Query, request.IncludePrerelease, request.Take, cancellationToken).ConfigureAwait(false);
        ReleaseOperation(cancellationToken);
        await transport.WriteAsync(PipeEnvelope.Create(
            MessageKinds.PackageSearchResults, envelope.CorrelationId, results), cancellationToken).ConfigureAwait(false);
    }
}
