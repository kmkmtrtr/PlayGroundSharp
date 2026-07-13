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
    string AssemblyName = "")
{
    public IReadOnlyList<SymbolExplorerParameter> ParameterItems => Parameters ?? [];
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
    public bool HasParameters => ParameterItems.Count > 0;
    public bool HasReturns => !string.IsNullOrWhiteSpace(Returns);
}
