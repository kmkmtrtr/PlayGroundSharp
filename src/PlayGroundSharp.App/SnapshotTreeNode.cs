using CommunityToolkit.Mvvm.ComponentModel;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

public sealed partial class SnapshotTreeNode : ObservableObject
{
    private const int MaximumLabelLength = 200;
    private readonly string name;
    private readonly ResultSnapshot snapshot;
    private IReadOnlyList<SnapshotTreeNode>? children;

    private SnapshotTreeNode(string name, ResultSnapshot snapshot, bool isExpanded = false)
    {
        this.name = name;
        this.snapshot = snapshot;
        this.isExpanded = isExpanded;
        var compact = SnapshotTextFormatter.FormatCompact(snapshot).ReplaceLineEndings(" ");
        if (compact.Length > MaximumLabelLength) compact = compact[..MaximumLabelLength] + "…";
        Label = $"{name} = {compact}";
        var capturedCount = snapshot.Properties?.Count ?? snapshot.Items?.Count;
        var countText = snapshot.TotalCount is null
            ? string.Empty
            : $"{Environment.NewLine}Count: {snapshot.TotalCount:N0}" +
              (capturedCount < snapshot.TotalCount ? $" ({capturedCount:N0} captured)" : string.Empty);
        Detail = $"{name}{Environment.NewLine}Type: {snapshot.TypeName ?? "unknown"}{countText}{Environment.NewLine}" +
                 SnapshotTextFormatter.FormatCompact(snapshot);
    }

    public string Label { get; }
    public string Detail { get; }
    [ObservableProperty] private bool isExpanded;
    public string CopyText => SnapshotTextFormatter.FormatFull(snapshot);
    public IReadOnlyList<SnapshotTreeNode> Children => children ??= CreateChildren();

    public static SnapshotTreeNode CreateRoot(ResultSnapshot snapshot) =>
        new(snapshot.TypeName ?? snapshot.Kind.ToString(), snapshot, isExpanded: true);

    private IReadOnlyList<SnapshotTreeNode> CreateChildren()
    {
        var result = new List<SnapshotTreeNode>();
        if (snapshot.Properties is not null)
            result.AddRange(snapshot.Properties.Select(static property => new SnapshotTreeNode(property.Name, property.Value)));
        if (snapshot.Items is not null)
            result.AddRange(snapshot.Items.Select(static (item, index) => new SnapshotTreeNode($"[{index}]", item)));
        return result;
    }
}
