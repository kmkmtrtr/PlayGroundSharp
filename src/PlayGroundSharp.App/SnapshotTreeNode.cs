using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

public sealed partial class SnapshotTreeNode : ObservableObject
{
    private const int MaximumLabelLength = 200;
    private readonly AppLanguageMode languageMode;
    private readonly string name;
    private readonly ResultSnapshot snapshot;
    private IReadOnlyList<SnapshotTreeNode>? children;

    private SnapshotTreeNode(
        string name,
        string path,
        ResultSnapshot snapshot,
        AppLanguageMode languageMode,
        bool isExpanded = false,
        IReadOnlyList<SnapshotTreeNode>? filteredChildren = null)
    {
        this.name = name;
        this.snapshot = snapshot;
        this.languageMode = languageMode;
        children = filteredChildren;
        Path = path;
        this.isExpanded = isExpanded;
        var compact = FormatCompact(snapshot, languageMode).ReplaceLineEndings(" ");
        if (compact.Length > MaximumLabelLength) compact = compact[..MaximumLabelLength] + "…";
        Label = $"{name} = {compact}";
        Detail = BuildDetail();
    }

    public string Label { get; }
    public string Detail { get; }
    public string Path { get; }
    [ObservableProperty] private bool isExpanded;
    public string CopyText => SnapshotTextFormatter.FormatFull(snapshot);
    public IReadOnlyList<SnapshotTreeNode> Children => children ??= CreateChildren();

    public static SnapshotTreeNode CreateRoot(ResultSnapshot snapshot, AppLanguageMode languageMode) =>
        new(snapshot.TypeName ?? snapshot.Kind.ToString(), "$", snapshot, languageMode, isExpanded: true);

    public static SnapshotTreeNode? CreateFilteredRoot(
        ResultSnapshot snapshot,
        AppLanguageMode languageMode,
        string query,
        out int matchCount)
    {
        matchCount = 0;
        if (string.IsNullOrWhiteSpace(query)) return CreateRoot(snapshot, languageMode);
        return CreateFilteredNode(
            snapshot.TypeName ?? snapshot.Kind.ToString(), "$", snapshot, languageMode,
            query.Trim(), ref matchCount);
    }

    private static SnapshotTreeNode? CreateFilteredNode(
        string name,
        string path,
        ResultSnapshot snapshot,
        AppLanguageMode languageMode,
        string query,
        ref int matchCount)
    {
        var isMatch = Contains(name, query) || Contains(path, query) ||
                      Contains(snapshot.TypeName, query) || Contains(snapshot.Display, query);
        if (isMatch) matchCount++;

        var matchingChildren = new List<SnapshotTreeNode>();
        if (snapshot.Properties is not null)
        {
            foreach (var property in snapshot.Properties)
            {
                var childPath = AppendPropertyPath(path, property.Name);
                if (CreateFilteredNode(property.Name, childPath, property.Value, languageMode, query, ref matchCount) is { } child)
                    matchingChildren.Add(child);
            }
        }
        if (snapshot.Items is not null)
        {
            for (var index = 0; index < snapshot.Items.Count; index++)
            {
                var childName = $"[{index}]";
                if (CreateFilteredNode(childName, path + childName, snapshot.Items[index], languageMode, query, ref matchCount) is { } child)
                    matchingChildren.Add(child);
            }
        }

        if (!isMatch && matchingChildren.Count == 0) return null;
        return new(name, path, snapshot, languageMode, isExpanded: true, matchingChildren);
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
        lines.Add(FormatCompact(snapshot, languageMode));
        return string.Join(Environment.NewLine, lines);
    }

    private IReadOnlyList<SnapshotTreeNode> CreateChildren()
    {
        var result = new List<SnapshotTreeNode>();
        if (snapshot.Properties is not null)
            result.AddRange(snapshot.Properties.Select(property =>
                new SnapshotTreeNode(property.Name, AppendPropertyPath(Path, property.Name), property.Value, languageMode)));
        if (snapshot.Items is not null)
            result.AddRange(snapshot.Items.Select((item, index) =>
                new SnapshotTreeNode($"[{index}]", $"{Path}[{index}]", item, languageMode)));
        return result;
    }

    private static string AppendPropertyPath(string path, string propertyName) =>
        IsSimpleIdentifier(propertyName)
            ? $"{path}.{propertyName}"
            : $"{path}[{JsonSerializer.Serialize(propertyName)}]";

    private static bool IsSimpleIdentifier(string value) =>
        value.Length > 0 && (char.IsLetter(value[0]) || value[0] == '_') &&
        value.Skip(1).All(static character => char.IsLetterOrDigit(character) || character == '_');

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true;

    private static string FormatCompact(ResultSnapshot value, AppLanguageMode languageMode)
    {
        if (value.Items is not null)
            return AppLocalization.Text(languageMode, "Inspector.ItemSummary", value.TotalCount ?? value.Items.Count) +
                   (value.IsTruncated ? " …" : string.Empty);
        if (value.Properties is not null)
            return AppLocalization.Text(languageMode, "Inspector.PropertySummary", value.TotalCount ?? value.Properties.Count) +
                   (value.IsTruncated ? " …" : string.Empty);
        return (value.Display ?? value.Kind.ToString()) + (value.IsTruncated ? " …" : string.Empty);
    }

    private string Text(string key, params object?[] arguments) =>
        AppLocalization.Text(languageMode, key, arguments);
}
