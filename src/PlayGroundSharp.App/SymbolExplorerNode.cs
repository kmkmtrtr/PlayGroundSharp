namespace PlayGroundSharp.App;

/// <summary>Represents one namespace or type in the hierarchical type explorer.</summary>
public sealed record SymbolExplorerNode(
    string Name,
    string Kind,
    string Glyph,
    string Detail,
    IReadOnlyList<SymbolExplorerNode> Children,
    bool IsExpanded = false);
