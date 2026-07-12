using System.Diagnostics;
using System.IO.Pipes;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.Worker;

/// <summary>Serves one App connection and serializes access to a ScriptSession.</summary>
public sealed class WorkerHost(string pipeName)
{
    private readonly ScriptSession session = new();
    private CancellationTokenSource? executionCancellation;

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
            switch (envelope.Kind)
            {
                case MessageKinds.Execute:
                    await ExecuteAsync(transport, envelope, hostToken).ConfigureAwait(false);
                    break;
                case MessageKinds.Cancel:
                    executionCancellation?.Cancel();
                    await transport.WriteAsync(PipeEnvelope.Create(MessageKinds.Cancelled, envelope.CorrelationId, new CancelledEvent(true)), hostToken).ConfigureAwait(false);
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

    private async Task ExecuteAsync(PipeTransport transport, PipeEnvelope envelope, CancellationToken hostToken)
    {
        var request = envelope.ReadPayload<ExecuteRequest>();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        executionCancellation = linked;
        await transport.WriteAsync(PipeEnvelope.Create(MessageKinds.Started, envelope.CorrelationId,
            new ExecutionStartedEvent(request.SubmissionIndex)), hostToken).ConfigureAwait(false);
        try
        {
            var result = await session.ExecuteAsync(request.SubmissionIndex, request.Code,
                text => transport.WriteAsync(PipeEnvelope.Create(MessageKinds.ConsoleOut, envelope.CorrelationId, new ConsoleEvent(text)), hostToken).GetAwaiter().GetResult(),
                text => transport.WriteAsync(PipeEnvelope.Create(MessageKinds.ConsoleError, envelope.CorrelationId, new ConsoleEvent(text)), hostToken).GetAwaiter().GetResult(),
                linked.Token).ConfigureAwait(false);
            if (result.Diagnostics.Count > 0)
                await transport.WriteAsync(PipeEnvelope.Create(MessageKinds.Diagnostics, envelope.CorrelationId, new DiagnosticsEvent(result.Diagnostics)), hostToken).ConfigureAwait(false);
            if (result.Snapshot is not null)
                await transport.WriteAsync(PipeEnvelope.Create(MessageKinds.Result, envelope.CorrelationId, new ResultEvent(request.SubmissionIndex, result.Snapshot)), hostToken).ConfigureAwait(false);
            if (result.Exception is not null)
                await transport.WriteAsync(PipeEnvelope.Create(MessageKinds.RuntimeError, envelope.CorrelationId, new RuntimeErrorEvent(result.Exception)), hostToken).ConfigureAwait(false);
            await transport.WriteAsync(PipeEnvelope.Create(MessageKinds.Completed, envelope.CorrelationId,
                new ExecutionCompletedEvent(request.SubmissionIndex, result.StateAccepted, Process.GetCurrentProcess().WorkingSet64)), hostToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await transport.WriteAsync(PipeEnvelope.Create(MessageKinds.Cancelled, envelope.CorrelationId, new CancelledEvent(true)), hostToken).ConfigureAwait(false);
        }
        finally
        {
            executionCancellation = null;
        }
    }

    private Task SendContextAsync(PipeTransport transport, Guid correlationId, CancellationToken cancellationToken)
    {
        var context = session.Context;
        return transport.WriteAsync(PipeEnvelope.Create(MessageKinds.SessionChanged, correlationId,
            new SessionChangedEvent(context.ReferencePaths, context.Imports)), cancellationToken);
    }
}
