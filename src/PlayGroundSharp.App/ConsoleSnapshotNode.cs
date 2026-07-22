using CommunityToolkit.Mvvm.ComponentModel;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

/// <summary>Chrome DevTools-style, lazily materialized node used by the inline transcript tree.</summary>
public sealed partial class ConsoleSnapshotNode : ObservableObject
{
    private const int GroupSize = 100;
    private const int DirectChildLimit = 120;
    private readonly ResultSnapshot? snapshot;
    private readonly Func<string>? copyTextFactory;
    private readonly Func<IReadOnlyList<ConsoleSnapshotNode>>? childrenFactory;
    private IReadOnlyList<ConsoleSnapshotNode>? children;

    private ConsoleSnapshotNode(
        string name,
        string preview,
        Func<IReadOnlyList<ConsoleSnapshotNode>>? childrenFactory = null,
        bool isExpanded = false,
        ResultSnapshot? snapshot = null,
        Func<string>? copyTextFactory = null)
    {
        Name = name;
        Preview = preview;
        this.childrenFactory = childrenFactory;
        this.snapshot = snapshot;
        this.copyTextFactory = copyTextFactory;
        this.isExpanded = isExpanded;
    }

    public string Name { get; }
    public string Separator => Name.Length == 0 ? string.Empty : ": ";
    public string Preview { get; }
    public string AccessibleLabel => Name.Length == 0 ? Preview : $"{Name}{Separator}{Preview}";
    public string CopyText => snapshot is not null
        ? SnapshotTextFormatter.FormatFull(snapshot)
        : copyTextFactory?.Invoke() ?? Preview;
    public bool HasName => Name.Length > 0;
    public IReadOnlyList<ConsoleSnapshotNode> Children =>
        children ??= childrenFactory?.Invoke() ?? [];

    [ObservableProperty] private bool isExpanded;

    public static ConsoleSnapshotNode CreateRoot(ResultSnapshot snapshot) =>
        Create(string.Empty, snapshot, isExpanded: ShouldExpandRoot(snapshot));

    private static bool ShouldExpandRoot(ResultSnapshot snapshot)
    {
        var count = snapshot.TotalCount ?? snapshot.Properties?.Count ?? snapshot.Items?.Count ?? 0;
        return count <= DirectChildLimit;
    }

    private static ConsoleSnapshotNode Create(string name, ResultSnapshot snapshot, bool isExpanded = false) =>
        new(name, FormatPreview(snapshot), HasChildren(snapshot) ? () => CreateChildren(snapshot) : null, isExpanded, snapshot);

    private static IReadOnlyList<ConsoleSnapshotNode> CreateChildren(ResultSnapshot snapshot)
    {
        if (snapshot.Properties is { } properties)
        {
            if (properties.Count > DirectChildLimit)
                return CreatePropertyGroups(properties);
            var nodes = properties.Select(property => Create(property.Name, property.Value)).ToList();
            AddCaptureLimitNode(nodes, snapshot, properties.Count);
            return nodes;
        }

        if (snapshot.Items is { } items)
        {
            if (items.Count > DirectChildLimit)
                return CreateItemGroups(items, snapshot);
            var nodes = items.Select((item, index) => Create($"[{index}]", item)).ToList();
            AddCaptureLimitNode(nodes, snapshot, items.Count);
            return nodes;
        }

        return [];
    }

    private static IReadOnlyList<ConsoleSnapshotNode> CreatePropertyGroups(IReadOnlyList<ResultProperty> properties)
    {
        var groups = new List<ConsoleSnapshotNode>();
        for (var offset = 0; offset < properties.Count; offset += GroupSize)
        {
            var start = offset;
            var count = Math.Min(GroupSize, properties.Count - start);
            groups.Add(new(
                $"properties {start:N0}–{start + count - 1:N0}",
                $"{{{count:N0} properties}}",
                () => properties.Skip(start).Take(count)
                    .Select(property => Create(property.Name, property.Value)).ToArray(),
                copyTextFactory: () => SnapshotTextFormatter.FormatFull(new ResultSnapshot(
                    SnapshotKind.Object,
                    $"{count:N0} properties",
                    null,
                    Properties: properties.Skip(start).Take(count).ToArray()))));
        }
        return groups;
    }

    private static IReadOnlyList<ConsoleSnapshotNode> CreateItemGroups(
        IReadOnlyList<ResultSnapshot> items,
        ResultSnapshot snapshot)
    {
        var groups = new List<ConsoleSnapshotNode>();
        for (var offset = 0; offset < items.Count; offset += GroupSize)
        {
            var start = offset;
            var count = Math.Min(GroupSize, items.Count - start);
            groups.Add(new(
                $"[{start:N0} … {start + count - 1:N0}]",
                $"{count:N0} items",
                () => items.Skip(start).Take(count)
                    .Select((item, index) => Create($"[{start + index}]", item)).ToArray(),
                copyTextFactory: () => SnapshotTextFormatter.FormatFull(new ResultSnapshot(
                    SnapshotKind.Sequence,
                    $"{count:N0} items",
                    snapshot.TypeName,
                    Items: items.Skip(start).Take(count).ToArray(),
                    TotalCount: count))));
        }
        AddCaptureLimitNode(groups, snapshot, items.Count);
        return groups;
    }

    private static void AddCaptureLimitNode(
        ICollection<ConsoleSnapshotNode> nodes,
        ResultSnapshot snapshot,
        int capturedCount)
    {
        if (!snapshot.IsTruncated) return;
        var remaining = snapshot.TotalCount is { } total
            ? Math.Max(0, total - capturedCount).ToString("N0")
            : "?";
        nodes.Add(new("…", $"{remaining} more not captured by Worker"));
    }

    private static bool HasChildren(ResultSnapshot snapshot) =>
        snapshot.Properties is { Count: > 0 } || snapshot.Items is { Count: > 0 };

    private static string FormatPreview(ResultSnapshot snapshot)
        => SnapshotTextFormatter.FormatCompact(snapshot);
}
