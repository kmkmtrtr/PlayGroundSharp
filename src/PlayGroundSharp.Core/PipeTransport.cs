using System.Text;
using System.Text.Json;

namespace PlayGroundSharp.Core;

/// <summary>Reads and writes JSON envelopes without depending on process standard streams.</summary>
public sealed class PipeTransport(Stream stream) : IAsyncDisposable
{
    private readonly StreamReader reader = new(stream, new UTF8Encoding(false), leaveOpen: true);
    private readonly StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public async Task WriteAsync(PipeEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(envelope, ProtocolJson.Options);
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task<PipeEnvelope?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null)
        {
            return null;
        }

        var envelope = JsonSerializer.Deserialize<PipeEnvelope>(line, ProtocolJson.Options)
            ?? throw new InvalidDataException("The pipe contained an invalid envelope.");
        if (envelope.Version != ProtocolConstants.Version)
        {
            throw new InvalidDataException($"IPC protocol {envelope.Version} is unsupported; expected {ProtocolConstants.Version}.");
        }

        return envelope;
    }

    public async ValueTask DisposeAsync()
    {
        writeLock.Dispose();
        await writer.DisposeAsync().ConfigureAwait(false);
        reader.Dispose();
    }
}
