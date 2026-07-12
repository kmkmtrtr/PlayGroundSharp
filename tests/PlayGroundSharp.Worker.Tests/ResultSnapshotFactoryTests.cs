using System.Text.Json.Nodes;
using PlayGroundSharp.Core;
using PlayGroundSharp.Worker;

namespace PlayGroundSharp.Worker.Tests;

public sealed class ResultSnapshotFactoryTests
{
    private readonly ResultSnapshotFactory factory = new();

    [Fact]
    public void HandlesScalarObjectSequenceJsonAndNull()
    {
        Assert.Equal(SnapshotKind.Null, factory.Create(null).Kind);
        Assert.Equal(SnapshotKind.Number, factory.Create(42).Kind);
        Assert.Equal(SnapshotKind.String, factory.Create("text").Kind);
        Assert.Equal(SnapshotKind.Object, factory.Create(new { Name = "Ada", Age = 30 }).Kind);
        Assert.Equal(SnapshotKind.Sequence, factory.Create(Enumerable.Range(1, 3)).Kind);
        Assert.Equal(SnapshotKind.Json, factory.Create(JsonNode.Parse("{\"answer\":42}")).Kind);
    }

    [Fact]
    public void DetectsCyclesAndMaximumItems()
    {
        var node = new Node();
        node.Next = node;
        var cycle = factory.Create(node);
        var sequence = factory.Create(Enumerable.Range(1, 101));

        Assert.Equal(SnapshotKind.Circular, cycle.Properties?.Single().Value.Kind);
        Assert.Equal(100, sequence.Items?.Count);
        Assert.True(sequence.IsTruncated);
    }

    [Fact]
    public void SnapshotsExceptions()
    {
        var snapshot = factory.Create(new InvalidOperationException("boom"));
        Assert.Equal(SnapshotKind.Exception, snapshot.Kind);
        Assert.Contains("boom", snapshot.Display);
    }

    private sealed class Node
    {
        public Node? Next { get; set; }
    }
}
