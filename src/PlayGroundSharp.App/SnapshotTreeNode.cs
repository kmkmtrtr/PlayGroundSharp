using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

public sealed class SnapshotTreeNode
{
    private const int MaximumLabelLength = 200;

    private SnapshotTreeNode(string label, string detail, IReadOnlyList<SnapshotTreeNode> children)
    {
        Label = label;
        Detail = detail;
        Children = children;
    }

    public string Label { get; }
    public string Detail { get; }
    public IReadOnlyList<SnapshotTreeNode> Children { get; }

    public static SnapshotTreeNode CreateRoot(ResultSnapshot snapshot) => Create(snapshot.TypeName ?? snapshot.Kind.ToString(), snapshot);

    private static SnapshotTreeNode Create(string name, ResultSnapshot snapshot)
    {
        var display = snapshot.Display ?? snapshot.Kind.ToString();
        var suffix = snapshot.IsTruncated ? " … (truncated)" : string.Empty;
        var detail = $"{name}{Environment.NewLine}Type: {snapshot.TypeName ?? "unknown"}{Environment.NewLine}{display}{suffix}";
        var compact = display.ReplaceLineEndings(" ");
        if (compact.Length > MaximumLabelLength) compact = compact[..MaximumLabelLength] + "…";
        var children = new List<SnapshotTreeNode>();
        if (snapshot.Properties is not null)
            children.AddRange(snapshot.Properties.Select(static property => Create(property.Name, property.Value)));
        if (snapshot.Items is not null)
            children.AddRange(snapshot.Items.Select(static (item, index) => Create($"[{index}]", item)));
        return new($"{name} = {compact}{suffix}", detail, children);
    }
}
