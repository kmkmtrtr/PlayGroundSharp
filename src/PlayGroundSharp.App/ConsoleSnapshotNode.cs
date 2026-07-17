using CommunityToolkit.Mvvm.ComponentModel;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

/// <summary>Chrome DevTools-style, lazily materialized node used by the inline transcript tree.</summary>
public sealed partial class ConsoleSnapshotNode : ObservableObject
{
    private const int GroupSize = 100;
    private const int DirectChildLimit = 120;
    private readonly Func<IReadOnlyList<ConsoleSnapshotNode>>? childrenFactory;
    private IReadOnlyList<ConsoleSnapshotNode>? children;

    private ConsoleSnapshotNode(
        string name,
        string preview,
        Func<IReadOnlyList<ConsoleSnapshotNode>>? childrenFactory = null,
        bool isExpanded = false)
    {
        Name = name;
        Preview = preview;
        this.childrenFactory = childrenFactory;
        this.isExpanded = isExpanded;
    }

    public string Name { get; }
    public string Separator => Name.Length == 0 ? string.Empty : ": ";
    public string Preview { get; }
    public bool HasName => Name.Length > 0;
    public IReadOnlyList<ConsoleSnapshotNode> Children =>
        children ??= childrenFactory?.Invoke() ?? [];

    [ObservableProperty] private bool isExpanded;

    public static ConsoleSnapshotNode CreateRoot(ResultSnapshot snapshot) =>
        Create(string.Empty, snapshot, isExpanded: true);

    private static ConsoleSnapshotNode Create(string name, ResultSnapshot snapshot, bool isExpanded = false) =>
        new(name, FormatPreview(snapshot), HasChildren(snapshot) ? () => CreateChildren(snapshot) : null, isExpanded);

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
                    .Select(property => Create(property.Name, property.Value)).ToArray()));
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
                    .Select((item, index) => Create($"[{start + index}]", item)).ToArray()));
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
    {
        if (snapshot.Properties is { } properties)
        {
            var members = properties.Take(4)
                .Select(property => $"{FormatPropertyName(property.Name)}: {FormatChildPreview(property.Value)}");
            var suffix = properties.Count > 4 || snapshot.IsTruncated ? ", …" : string.Empty;
            return $"{{{string.Join(", ", members)}{suffix}}}";
        }

        if (snapshot.Items is { } items)
        {
            var count = snapshot.TotalCount ?? items.Count;
            var members = items.Take(6).Select(FormatChildPreview);
            var suffix = items.Count > 6 || snapshot.IsTruncated ? ", …" : string.Empty;
            return $"({count:N0}) [{string.Join(", ", members)}{suffix}]";
        }

        return FormatScalar(snapshot);
    }

    private static string FormatChildPreview(ResultSnapshot snapshot)
    {
        if (snapshot.Items is { } items)
            return $"Array({snapshot.TotalCount ?? items.Count:N0})";
        if (snapshot.Properties is not null)
            return "{…}";
        return FormatScalar(snapshot);
    }

    private static string FormatScalar(ResultSnapshot snapshot)
    {
        var display = snapshot.Display ?? snapshot.Kind.ToString();
        var labelTruncated = display.Length > 160;
        if (labelTruncated) display = display[..160];
        var value = snapshot.Kind is SnapshotKind.String or SnapshotKind.DateTime or SnapshotKind.Guid
            ? SnapshotTextFormatter.QuoteJsonString(display)
            : display;
        return labelTruncated || snapshot.IsTruncated ? value + "…" : value;
    }

    private static string FormatPropertyName(string value) =>
        value.Length > 0 && (char.IsLetter(value[0]) || value[0] == '_') &&
        value.Skip(1).All(static character => char.IsLetterOrDigit(character) || character == '_')
            ? value
            : SnapshotTextFormatter.QuoteJsonString(value);
}
