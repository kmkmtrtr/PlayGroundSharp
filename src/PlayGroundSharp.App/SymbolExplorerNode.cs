namespace PlayGroundSharp.App;

/// <summary>Represents one documented parameter in the symbol explorer.</summary>
public sealed record SymbolExplorerParameter(string Name, string TypeName, string Summary);

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
    IReadOnlyList<string>? InheritedTypes = null)
{
    public IReadOnlyList<SymbolExplorerParameter> ParameterItems => Parameters ?? [];
    public IReadOnlyList<string> InheritedTypeItems => InheritedTypes ?? [];
    public string KindLabel => Kind switch
    {
        "namespace" => "Namespace",
        "class" => "Class",
        "record" => "Record",
        "record struct" => "Record struct",
        "interface" => "Interface",
        "struct" => "Struct",
        "enum" => "Enum",
        "delegate" => "Delegate",
        "method" => "Method",
        "constructor" => "Constructor",
        _ => "Type"
    };
    public string InheritanceDisplay => string.Join(", ", InheritedTypeItems);
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
    public bool HasInheritance => InheritedTypeItems.Count > 0;
    public bool HasParameters => ParameterItems.Count > 0;
    public bool HasReturns => !string.IsNullOrWhiteSpace(Returns);
    public bool HasOnlineDocumentation => !string.IsNullOrWhiteSpace(DocumentationPath);
}
