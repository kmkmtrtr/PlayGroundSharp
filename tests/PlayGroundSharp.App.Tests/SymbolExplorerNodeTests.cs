namespace PlayGroundSharp.App.Tests;

public sealed class SymbolExplorerNodeTests
{
    [Fact]
    public void AccessibleTextIncludesKindInheritanceSignatureAndSummary()
    {
        var node = new SymbolExplorerNode(
            "Widget",
            "class",
            "C",
            "",
            [],
            Signature: "public class Widget : BaseWidget, IWidget",
            Summary: "Represents a widget.",
            InheritedTypes: ["BaseWidget", "IWidget"]);

        Assert.Equal("Widget — Class — BaseWidget, IWidget", node.AccessibleLabel);
        Assert.Equal(
            $"public class Widget : BaseWidget, IWidget{Environment.NewLine}Represents a widget.",
            node.AccessibleHelpText);
    }

    [Fact]
    public void EnumValueAccessibleTextKeepsItsNumericValue()
    {
        var node = new SymbolExplorerNode("Ready = 5", "enum member", "V", "", []);

        Assert.Equal("Ready = 5 — Enum value", node.AccessibleLabel);
        Assert.Empty(node.AccessibleHelpText);
    }
}
