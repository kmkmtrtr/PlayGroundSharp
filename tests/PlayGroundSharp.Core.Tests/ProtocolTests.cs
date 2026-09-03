using System.IO.Pipes;
using System.Text.Json;
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
    public async Task DisposeWaitsForAnInProgressWriteAndRejectsLaterWrites()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stream = new BlockingWriteStream();
        var transport = new PipeTransport(stream);
        var envelope = PipeEnvelope.Create(MessageKinds.Execute, Guid.NewGuid(), new ExecuteRequest(1, "1 + 2"));

        var write = transport.WriteAsync(envelope, timeout.Token);
        await stream.WriteStarted.WaitAsync(timeout.Token);

        var dispose = transport.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);

        stream.ReleaseWrite();
        await write;
        await dispose;

        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.WriteAsync(envelope, timeout.Token));
    }

    [Fact]
    public void PackageSearchResultsRoundTripAsNeutralDtos()
    {
        var payload = new PackageSearchResultsEvent("json", 1,
            [new("Example.Package", "1.2.3", "Description", "Author", 42, true, ["1.0.0", "1.2.3"])]);

        var envelope = PipeEnvelope.Create(MessageKinds.PackageSearchResults, Guid.NewGuid(), payload);
        var restored = envelope.ReadPayload<PackageSearchResultsEvent>();

        Assert.Equal("json", restored.Query);
        var package = Assert.Single(restored.Packages);
        Assert.Equal("Example.Package", package.PackageId);
        Assert.True(package.IsVerified);
        Assert.Equal(["1.0.0", "1.2.3"], package.Versions);
    }

    [Fact]
    public void PackageSearchMetadataAcceptsPayloadWithoutVersionList()
    {
        var restored = JsonSerializer.Deserialize<NuGetPackageInfo>(
            """{"packageId":"Example.Package","version":"1.2.3","description":"","authors":"","totalDownloads":0,"isVerified":false}""",
            ProtocolJson.Options);

        Assert.NotNull(restored);
        Assert.Null(restored.Versions);
    }

    [Fact]
    public void DiagnosticsEventAcceptsPayloadWithoutTotalCount()
    {
        var restored = JsonSerializer.Deserialize<DiagnosticsEvent>(
            """{"diagnostics":[]}""",
            ProtocolJson.Options);

        Assert.NotNull(restored);
        Assert.Empty(restored.Diagnostics);
        Assert.Null(restored.TotalCount);
    }

    [Fact]
    public void DiagnosticsEventPreservesTotalCount()
    {
        var payload = new DiagnosticsEvent(
            [new("CS0103", DiagnosticLevel.Error, "Unknown name", 1, 1, 1, 4)],
            250);

        var envelope = PipeEnvelope.Create(MessageKinds.Diagnostics, Guid.NewGuid(), payload);
        var restored = envelope.ReadPayload<DiagnosticsEvent>();

        Assert.Single(restored.Diagnostics);
        Assert.Equal(250, restored.TotalCount);
    }

    [Fact]
    public void VariablesEventPreservesCompletionTypeExpressions()
    {
        var payload = new VariablesEvent(
            [new(
                "pair",
                "System.ValueTuple`2[System.Int32,System.Int32]",
                new(SnapshotKind.Object, "2 members", "System.ValueTuple`2"),
                false,
                "(int index, int delay)")],
            []);

        var envelope = PipeEnvelope.Create(MessageKinds.Variables, Guid.NewGuid(), payload);
        var variable = Assert.Single(envelope.ReadPayload<VariablesEvent>().Variables);

        Assert.Equal("(int index, int delay)", variable.CompletionTypeExpression);
    }

    [Fact]
    public void InspectionResultRoundTripsSnapshotAndDiagnostics()
    {
        var payload = new InspectionResultEvent(
            new(SnapshotKind.Number, "42", "System.Int32"),
            [new("CS0001", DiagnosticLevel.Warning, "warning", 1, 1, 1, 2)],
            null,
            1);

        var envelope = PipeEnvelope.Create(MessageKinds.InspectionResult, Guid.NewGuid(), payload);
        var restored = envelope.ReadPayload<InspectionResultEvent>();

        Assert.Equal("42", restored.Snapshot?.Display);
        Assert.Single(restored.Diagnostics);
        Assert.Equal(1, restored.TotalDiagnosticCount);
    }

    [Fact]
    public void LegacyDiagnosticsReaderIgnoresTotalCount()
    {
        var envelope = PipeEnvelope.Create(
            MessageKinds.Diagnostics,
            Guid.NewGuid(),
            new DiagnosticsEvent(
                [new("CS0103", DiagnosticLevel.Error, "Unknown name", 1, 1, 1, 4)],
                250));

        var restored = envelope.ReadPayload<LegacyDiagnosticsEvent>();

        Assert.Single(restored.Diagnostics);
    }

    [Fact]
    public void ResultSnapshotCountMetadataRoundTrips()
    {
        var snapshot = new ResultSnapshot(
            SnapshotKind.Sequence,
            "3 items",
            "System.Int32[]",
            Items: [
                new(SnapshotKind.Number, "1", "System.Int32"),
                new(SnapshotKind.Number, "2", "System.Int32"),
                new(SnapshotKind.Number, "3", "System.Int32")
            ],
            TotalCount: 3);

        var envelope = PipeEnvelope.Create(MessageKinds.Result, Guid.NewGuid(), new ResultEvent(1, snapshot));
        var restored = envelope.ReadPayload<ResultEvent>().Snapshot;

        Assert.Equal(3, restored.TotalCount);
        Assert.Equal(["1", "2", "3"], restored.Items!.Select(static item => item.Display));
    }

    [Fact]
    public void LegacyInspectionRequestDefaultsToOrdinarySnapshot()
    {
        var restored = JsonSerializer.Deserialize<InspectExpressionRequest>(
            """{"code":"rows"}""",
            ProtocolJson.Options);

        Assert.NotNull(restored);
        Assert.False(restored.ForDataInference);
    }

    [Fact]
    public void LegacySnapshotDefaultsInferenceMetadataToFalse()
    {
        var restored = JsonSerializer.Deserialize<ResultSnapshot>(
            """{"kind":7,"display":"1 property","typeName":"Json","properties":[{"name":"id","value":{"kind":1,"display":"1","typeName":"Json"}}]}""",
            ProtocolJson.Options);

        Assert.NotNull(restored);
        Assert.False(restored.IsInferredNullable);
        Assert.False(Assert.Single(restored.Properties!).IsOptional);
    }

    [Fact]
    public void StreamedResultPreservesItsSourceIndex()
    {
        var payload = new StreamedResultEvent(
            3,
            7,
            new(SnapshotKind.Number, "42", "System.Int32"));

        var envelope = PipeEnvelope.Create(MessageKinds.StreamedResult, Guid.NewGuid(), payload);
        var restored = envelope.ReadPayload<StreamedResultEvent>();

        Assert.Equal(3, restored.SubmissionIndex);
        Assert.Equal(7, restored.SourceIndex);
        Assert.Equal("42", restored.Snapshot.Display);
    }

    [Fact]
    public void ResultSnapshotItemIndexesRoundTrip()
    {
        var snapshot = new ResultSnapshot(
            SnapshotKind.Sequence,
            "2 items",
            null,
            Items:
            [
                new(SnapshotKind.Number, "20", "System.Int32"),
                new(SnapshotKind.Number, "10", "System.Int32")
            ],
            TotalCount: 2,
            ItemIndexes: [1, 0]);

        var envelope = PipeEnvelope.Create(MessageKinds.Result, Guid.NewGuid(), new ResultEvent(1, snapshot));
        var restored = envelope.ReadPayload<ResultEvent>().Snapshot;

        Assert.Equal([1, 0], restored.ItemIndexes);
    }

    [Fact]
    public void ResultSnapshotClrTypeMetadataRoundTrips()
    {
        var snapshot = new ResultSnapshot(
            SnapshotKind.Object,
            "1 member",
            "Row",
            Properties:
            [
                new(
                    "Address",
                    new(
                        SnapshotKind.Object,
                        "value",
                        "System.Net.IPAddress",
                        TypeExpression: "global::System.Net.IPAddress",
                        IsReferenceType: true),
                    "global::System.Net.IPAddress",
                    true)
            ]);

        var envelope = PipeEnvelope.Create(MessageKinds.Result, Guid.NewGuid(), new ResultEvent(1, snapshot));
        var property = Assert.Single(envelope.ReadPayload<ResultEvent>().Snapshot.Properties!);

        Assert.Equal("global::System.Net.IPAddress", property.Value.TypeExpression);
        Assert.True(property.Value.IsReferenceType);
        Assert.Equal("global::System.Net.IPAddress", property.DeclaredTypeExpression);
        Assert.True(property.IsReadable);
    }

    [Fact]
    public void SnapshotJsonExportIsValidAndMarksTruncatedCollections()
    {
        var snapshot = new ResultSnapshot(
            SnapshotKind.Object,
            "2 properties",
            "Example",
            Properties:
            [
                new("answer", new(SnapshotKind.Number, "42", "System.Int32")),
                new("states", new(
                    SnapshotKind.Sequence,
                    "3 items",
                    "State[]",
                    Items:
                    [
                        new(SnapshotKind.Enum, "Ready", "State"),
                        new(SnapshotKind.Boolean, "true", "System.Boolean")
                    ],
                    IsTruncated: true,
                    TotalCount: 3))
            ]);

        using var document = JsonDocument.Parse(SnapshotJsonFormatter.Format(snapshot));

        Assert.Equal(42, document.RootElement.GetProperty("answer").GetInt32());
        var states = document.RootElement.GetProperty("states");
        Assert.Equal("Ready", states[0].GetString());
        Assert.True(states[1].GetBoolean());
        var metadata = states[2].GetProperty("$playgroundSharp");
        Assert.True(metadata.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, metadata.GetProperty("capturedCount").GetInt32());
        Assert.Equal(3, metadata.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public void SnapshotJsonExportKeepsReadableUnicodeAndHtmlCharacters()
    {
        var snapshot = new ResultSnapshot(
            SnapshotKind.Object,
            "1 property",
            "Example",
            Properties: [new("message", new(SnapshotKind.String, "日本語　<tag>", "System.String"))]);

        var json = SnapshotJsonFormatter.Format(snapshot);
        using var document = JsonDocument.Parse(json);

        Assert.Contains("日本語　<tag>", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("日本語　<tag>", document.RootElement.GetProperty("message").GetString());
    }

    private sealed class BlockingWriteStream : Stream
    {
        private readonly TaskCompletionSource writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WriteStarted => writeStarted.Task;

        public void ReleaseWrite() => releaseWrite.TrySetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            writeStarted.TrySetResult();
            await releaseWrite.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed record LegacyDiagnosticsEvent(IReadOnlyList<DiagnosticInfo> Diagnostics);
}
