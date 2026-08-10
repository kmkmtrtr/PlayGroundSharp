namespace PlayGroundSharp.App;

/// <summary>Represents one documented parameter in the symbol explorer.</summary>
public sealed record SymbolExplorerParameter(string Name, string TypeName, string Summary);

/// <summary>Represents one navigable type relationship in the symbol explorer.</summary>
public sealed record SymbolExplorerRelation(
    string SymbolId,
    string Name,
    string Kind,
    string RelationKind,
    bool CanNavigate,
    string? TargetFullName,
    string AssemblyName);

/// <summary>Represents one namespace or type in the hierarchical type explorer.</summary>
public sealed record SymbolExplorerNode(
    string Name,
    string Kind,
    string Glyph,
    string Detail,
    IReadOnlyList<SymbolExplorerNode> Children,
    bool IsExpanded = false,
    string Signature = "",
    string Summary = "",
    IReadOnlyList<SymbolExplorerParameter>? Parameters = null,
    string Returns = "",
    string AssemblyName = "",
    string? DocumentationPath = null,
    IReadOnlyList<string>? InheritedTypes = null,
    string? SymbolId = null,
    IReadOnlyList<SymbolExplorerRelation>? ParentRelations = null,
    IReadOnlyList<SymbolExplorerRelation>? DerivedRelations = null)
{
    public IReadOnlyList<SymbolExplorerParameter> ParameterItems => Parameters ?? [];
    public IReadOnlyList<string> InheritedTypeItems => InheritedTypes ?? [];
    public IReadOnlyList<SymbolExplorerRelation> ParentRelationItems => ParentRelations ?? [];
    public IReadOnlyList<SymbolExplorerRelation> DerivedRelationItems => DerivedRelations ?? [];
    public string KindLabel => Kind switch
    {
        "namespace" => "Namespace",
        "class" => "Class",
        "record" => "Record",
        "record struct" => "Record struct",
        "interface" => "Interface",
        "struct" => "Struct",
        "enum" => "Enum",
        "enum member" => "Enum value",
        "delegate" => "Delegate",
        "method" => "Method",
        "constructor" => "Constructor",
        _ => "Type"
    };
    public string InheritanceDisplay => string.Join(", ", InheritedTypeItems);
    public string AccessibleLabel => HasInheritance
        ? $"{Name} — {KindLabel} — {InheritanceDisplay}"
        : $"{Name} — {KindLabel}";
    public string AccessibleHelpText => string.Join(
        Environment.NewLine,
        new[] { Signature, Summary }.Where(static value => !string.IsNullOrWhiteSpace(value)));
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
    public bool HasInheritance => InheritedTypeItems.Count > 0;
    public bool HasParentRelations => ParentRelationItems.Count > 0;
    public bool HasDerivedRelations => DerivedRelationItems.Count > 0;
    public bool HasParameters => ParameterItems.Count > 0;
    public bool HasReturns => !string.IsNullOrWhiteSpace(Returns);
    public bool HasOnlineDocumentation => !string.IsNullOrWhiteSpace(DocumentationPath);

    public static IReadOnlyList<SymbolExplorerNode>? FindPathBySymbolId(
        IEnumerable<SymbolExplorerNode> roots,
        string symbolId)
    {
        foreach (var root in roots)
        {
            if (root.SymbolId == symbolId) return [root];
            if (FindPathBySymbolId(root.Children, symbolId) is not { } childPath) continue;
            return [root, .. childPath];
        }
        return null;
    }
}
