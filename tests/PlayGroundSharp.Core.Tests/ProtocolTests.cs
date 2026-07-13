using System.IO.Pipes;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.Core.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task EnvelopeRoundTripsOverPipeTransport()
    {
        var pipeName = $"pgs-test-{Guid.NewGuid():N}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connect = client.ConnectAsync(timeout.Token);
        await server.WaitForConnectionAsync(timeout.Token);
        await connect;
        await using var sender = new PipeTransport(client);
        await using var receiver = new PipeTransport(server);
        var correlationId = Guid.NewGuid();

        var receive = receiver.ReadAsync(timeout.Token);
        await sender.WriteAsync(PipeEnvelope.Create(MessageKinds.Execute, correlationId, new ExecuteRequest(1, "1 + 2")), timeout.Token);
        var envelope = await receive;

        Assert.NotNull(envelope);
        Assert.Equal(correlationId, envelope.CorrelationId);
        Assert.Equal("1 + 2", envelope.ReadPayload<ExecuteRequest>().Code);
    }

    [Fact]
    public void PackageSearchResultsRoundTripAsNeutralDtos()
    {
        var payload = new PackageSearchResultsEvent("json", 1,
            [new("Example.Package", "1.2.3", "Description", "Author", 42, true)]);

        var envelope = PipeEnvelope.Create(MessageKinds.PackageSearchResults, Guid.NewGuid(), payload);
        var restored = envelope.ReadPayload<PackageSearchResultsEvent>();

        Assert.Equal("json", restored.Query);
        var package = Assert.Single(restored.Packages);
        Assert.Equal("Example.Package", package.PackageId);
        Assert.True(package.IsVerified);
    }
}
