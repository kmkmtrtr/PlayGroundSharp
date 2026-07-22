using CommunityToolkit.Mvvm.ComponentModel;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

public sealed partial class SnapshotTreeNode : ObservableObject
{
    private const int MaximumLabelLength = 200;
    private const int MaximumFilteredMatches = 250;
    private const int DirectChildLimit = 120;
    private const int GroupSize = 100;
    private readonly AppLanguageMode languageMode;
    private readonly string name;
    private readonly ResultSnapshot snapshot;
    private readonly Func<IReadOnlyList<SnapshotTreeNode>>? childrenFactory;
    private IReadOnlyList<SnapshotTreeNode>? children;

    private SnapshotTreeNode(
        string name,
        string path,
        ResultSnapshot snapshot,
        AppLanguageMode languageMode,
        bool isExpanded = false,
        bool isSearchMatch = false,
        IReadOnlyList<SnapshotTreeNode>? filteredChildren = null,
        Func<IReadOnlyList<SnapshotTreeNode>>? childrenFactory = null)
    {
        this.name = name;
        this.snapshot = snapshot;
        this.languageMode = languageMode;
        children = filteredChildren;
        this.childrenFactory = childrenFactory;
        Path = path;
        IsSearchMatch = isSearchMatch;
        this.isExpanded = isExpanded;
        var compact = SnapshotTextFormatter.FormatCompact(snapshot).ReplaceLineEndings(" ");
        if (compact.Length > MaximumLabelLength) compact = compact[..MaximumLabelLength] + "…";
        Label = $"{name} = {compact}";
        Detail = BuildDetail();
    }

    public string Label { get; }
    public string Detail { get; }
    public string Path { get; }
    public bool IsSearchMatch { get; }
    [ObservableProperty] private bool isExpanded;
    [ObservableProperty] private bool isSelected;
    public string CopyText => SnapshotTextFormatter.FormatFull(snapshot);
    public IReadOnlyList<SnapshotTreeNode> Children => children ??= childrenFactory?.Invoke() ?? CreateChildren();

    public static SnapshotTreeNode CreateRoot(ResultSnapshot snapshot, AppLanguageMode languageMode) =>
        new(snapshot.TypeName ?? snapshot.Kind.ToString(), "$", snapshot, languageMode, isExpanded: true);

    public static SnapshotTreeNode? CreateFilteredRoot(
        ResultSnapshot snapshot,
        AppLanguageMode languageMode,
        string query,
        out int matchCount,
        out int displayedMatchCount,
        CancellationToken cancellationToken = default)
    {
        matchCount = 0;
        displayedMatchCount = 0;
        if (string.IsNullOrWhiteSpace(query)) return CreateRoot(snapshot, languageMode);
        return CreateFilteredNode(
            snapshot.TypeName ?? snapshot.Kind.ToString(), "$", snapshot, languageMode,
            query.Trim(), ref matchCount, ref displayedMatchCount, cancellationToken);
    }

    private static SnapshotTreeNode? CreateFilteredNode(
        string name,
        string path,
        ResultSnapshot snapshot,
        AppLanguageMode languageMode,
        string query,
        ref int matchCount,
        ref int displayedMatchCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var isMatch = Contains(name, query) || Contains(path, query) ||
                      Contains(snapshot.TypeName, query) || Contains(snapshot.Display, query);
        var includeSelf = false;
        if (isMatch)
        {
            matchCount++;
            if (displayedMatchCount < MaximumFilteredMatches)
            {
                displayedMatchCount++;
                includeSelf = true;
            }
        }

        List<SnapshotTreeNode>? matchingChildren = null;
        if (snapshot.Properties is not null)
        {
            foreach (var property in snapshot.Properties)
            {
                var childPath = AppendPropertyPath(path, property.Name);
                if (CreateFilteredNode(property.Name, childPath, property.Value, languageMode, query,
                        ref matchCount, ref displayedMatchCount, cancellationToken) is { } child)
                    (matchingChildren ??= []).Add(child);
            }
        }
        if (snapshot.Items is not null)
        {
            for (var index = 0; index < snapshot.Items.Count; index++)
            {
                var childName = $"[{index}]";
                if (CreateFilteredNode(childName, path + childName, snapshot.Items[index], languageMode, query,
                        ref matchCount, ref displayedMatchCount, cancellationToken) is { } child)
                    (matchingChildren ??= []).Add(child);
            }
        }

        if (!includeSelf && matchingChildren is null) return null;
        return new(
            name,
            path,
            snapshot,
            languageMode,
            isExpanded: true,
            isSearchMatch: includeSelf,
            filteredChildren: GroupFilteredChildren(matchingChildren ?? [], path, snapshot, languageMode));
    }

    private string BuildDetail()
    {
        var capturedCount = snapshot.Properties?.Count ?? snapshot.Items?.Count;
        var lines = new List<string>
        {
            $"{Text("Inspector.Path")}: {Path}",
            $"{Text("Inspector.Type")}: {snapshot.TypeName ?? Text("Inspector.UnknownType")}"
        };
        if (snapshot.TotalCount is { } totalCount)
        {
            var count = $"{Text("Inspector.Count")}: {totalCount:N0}";
            if (capturedCount < totalCount)
                count += $" ({Text("Inspector.Captured", capturedCount ?? 0)})";
            lines.Add(count);
        }
        lines.Add(string.Empty);
        lines.Add(SnapshotTextFormatter.FormatCompact(snapshot));
        return string.Join(Environment.NewLine, lines);
    }

    private IReadOnlyList<SnapshotTreeNode> CreateChildren()
    {
        var result = new List<SnapshotTreeNode>();
        if (snapshot.Properties is not null)
        {
            if (snapshot.Properties.Count > DirectChildLimit)
                return CreatePropertyGroups(snapshot.Properties);
            result.AddRange(snapshot.Properties.Select(property =>
                new SnapshotTreeNode(property.Name, AppendPropertyPath(Path, property.Name), property.Value, languageMode)));
        }
        if (snapshot.Items is not null)
        {
            if (snapshot.Items.Count > DirectChildLimit)
                return CreateItemGroups(snapshot.Items);
            result.AddRange(snapshot.Items.Select((item, index) =>
                new SnapshotTreeNode($"[{index}]", $"{Path}[{index}]", item, languageMode)));
        }
        return result;
    }

    private IReadOnlyList<SnapshotTreeNode> CreatePropertyGroups(IReadOnlyList<ResultProperty> properties)
    {
        var groups = new List<SnapshotTreeNode>();
        for (var offset = 0; offset < properties.Count; offset += GroupSize)
        {
            var start = offset;
            var groupProperties = properties.Skip(start).Take(Math.Min(GroupSize, properties.Count - start)).ToArray();
            var end = start + groupProperties.Length - 1;
            var groupSnapshot = snapshot with
            {
                Display = $"{groupProperties.Length:N0} properties",
                Properties = groupProperties,
                Items = null,
                IsTruncated = false,
                TotalCount = groupProperties.Length
            };
            groups.Add(new(
                Text("Inspector.PropertyRange", start, end),
                $"{Path}.{{{start}…{end}}}",
                groupSnapshot,
                languageMode,
                childrenFactory: () => groupProperties.Select(property =>
                    new SnapshotTreeNode(property.Name, AppendPropertyPath(Path, property.Name), property.Value, languageMode)).ToArray()));
        }
        return groups;
    }

    private IReadOnlyList<SnapshotTreeNode> CreateItemGroups(IReadOnlyList<ResultSnapshot> items)
    {
        var groups = new List<SnapshotTreeNode>();
        for (var offset = 0; offset < items.Count; offset += GroupSize)
        {
            var start = offset;
            var groupItems = items.Skip(start).Take(Math.Min(GroupSize, items.Count - start)).ToArray();
            var end = start + groupItems.Length - 1;
            var groupSnapshot = snapshot with
            {
                Display = $"{groupItems.Length:N0} items",
                Properties = null,
                Items = groupItems,
                IsTruncated = false,
                TotalCount = groupItems.Length
            };
            groups.Add(new(
                Text("Inspector.ItemRange", start, end),
                $"{Path}[{start}…{end}]",
                groupSnapshot,
                languageMode,
                childrenFactory: () => groupItems.Select((item, index) =>
                    new SnapshotTreeNode($"[{start + index}]", $"{Path}[{start + index}]", item, languageMode)).ToArray()));
        }
        return groups;
    }

    private static IReadOnlyList<SnapshotTreeNode> GroupFilteredChildren(
        IReadOnlyList<SnapshotTreeNode> matchingChildren,
        string parentPath,
        ResultSnapshot parentSnapshot,
        AppLanguageMode languageMode)
    {
        if (matchingChildren.Count <= DirectChildLimit) return matchingChildren;
        var groups = new List<SnapshotTreeNode>();
        for (var offset = 0; offset < matchingChildren.Count; offset += GroupSize)
        {
            var start = offset;
            var groupChildren = matchingChildren.Skip(start)
                .Take(Math.Min(GroupSize, matchingChildren.Count - start)).ToArray();
            var end = start + groupChildren.Length - 1;
            var groupSnapshot = parentSnapshot.Properties is not null
                ? parentSnapshot with
                {
                    Display = $"{groupChildren.Length:N0} matches",
                    Properties = groupChildren.Select(child => new ResultProperty(child.name, child.snapshot)).ToArray(),
                    Items = null,
                    IsTruncated = false,
                    TotalCount = groupChildren.Length
                }
                : parentSnapshot with
                {
                    Display = $"{groupChildren.Length:N0} matches",
                    Properties = null,
                    Items = groupChildren.Select(child => child.snapshot).ToArray(),
                    IsTruncated = false,
                    TotalCount = groupChildren.Length
                };
            groups.Add(new(
                AppLocalization.Text(languageMode, "Inspector.MatchRange", start + 1, end + 1),
                $"{parentPath}.matches[{start + 1}…{end + 1}]",
                groupSnapshot,
                languageMode,
                childrenFactory: () => groupChildren));
        }
        return groups;
    }

    private static string AppendPropertyPath(string path, string propertyName) =>
        IsSimpleIdentifier(propertyName)
            ? $"{path}.{propertyName}"
            : $"{path}[{SnapshotTextFormatter.QuoteJsonString(propertyName)}]";

    private static bool IsSimpleIdentifier(string value) =>
        value.Length > 0 && (char.IsLetter(value[0]) || value[0] == '_') &&
        value.Skip(1).All(static character => char.IsLetterOrDigit(character) || character == '_');

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true;

    private string Text(string key, params object?[] arguments) =>
        AppLocalization.Text(languageMode, key, arguments);
}
