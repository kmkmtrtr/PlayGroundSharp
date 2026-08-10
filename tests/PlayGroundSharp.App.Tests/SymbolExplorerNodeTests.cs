using PlayGroundSharp.LanguageService;

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

    [Fact]
    public void BuildsAndSearchesPropertyNodesWithDedicatedGlyph()
    {
        var type = Entry("Widget", "widget-id", "class");
        var property = new SymbolExplorerEntry(
            "Example.Types",
            "CreatedAt",
            "CreatedAt : DateTime",
            "property",
            "Example",
            "Widget",
            "DateTime Widget.CreatedAt { get; init; }",
            "Gets the creation time.",
            [],
            string.Empty,
            null,
            []);

        var roots = MainViewModel.BuildTypeExplorerItems([type, property], "DateTime");
        var propertyNode = Assert.Single(
            Assert.Single(
                Assert.Single(
                    Assert.Single(roots).Children).Children).Children);

        Assert.Equal("CreatedAt : DateTime", propertyNode.Name);
        Assert.Equal("property", propertyNode.Kind);
        Assert.Equal("P", propertyNode.Glyph);
        Assert.Equal("Property", propertyNode.KindLabel);
    }

    [Fact]
    public void BuildsBidirectionalNavigableTypeRelationships()
    {
        var baseType = Entry("BaseWidget", "base-id", "class");
        var sameNamedBaseType = Entry("BaseWidget", "other-base-id", "class");
        var contract = Entry("IWidget", "interface-id", "interface");
        var widget = Entry(
            "Widget",
            "widget-id",
            "class",
            [
                new("base-id", "BaseWidget", "class"),
                new("interface-id", "IWidget", "interface")
            ]);
        var externalChild = Entry(
            "ExternalChild",
            "external-child-id",
            "class",
            [new("missing-id", "MissingBase", "class")]);

        var roots = MainViewModel.BuildTypeExplorerItems([widget, contract, externalChild, sameNamedBaseType, baseType]);
        var widgetNode = SymbolExplorerNode.FindPathBySymbolId(roots, "widget-id")!.Last();
        var baseNode = SymbolExplorerNode.FindPathBySymbolId(roots, "base-id")!.Last();
        var interfaceNode = SymbolExplorerNode.FindPathBySymbolId(roots, "interface-id")!.Last();
        var externalNode = SymbolExplorerNode.FindPathBySymbolId(roots, "external-child-id")!.Last();
        var sameNamedBaseNode = SymbolExplorerNode.FindPathBySymbolId(roots, "other-base-id")!.Last();

        Assert.Collection(
            widgetNode.ParentRelationItems,
            relation =>
            {
                Assert.Equal("base-id", relation.SymbolId);
                Assert.Equal("base", relation.RelationKind);
                Assert.True(relation.CanNavigate);
            },
            relation =>
            {
                Assert.Equal("interface-id", relation.SymbolId);
                Assert.Equal("interface", relation.RelationKind);
                Assert.True(relation.CanNavigate);
            });
        Assert.Equal("widget-id", Assert.Single(baseNode.DerivedRelationItems).SymbolId);
        Assert.Equal("derived", Assert.Single(baseNode.DerivedRelationItems).RelationKind);
        Assert.Empty(sameNamedBaseNode.DerivedRelationItems);
        Assert.Equal("widget-id", Assert.Single(interfaceNode.DerivedRelationItems).SymbolId);
        Assert.Equal("implementation", Assert.Single(interfaceNode.DerivedRelationItems).RelationKind);
        Assert.False(Assert.Single(externalNode.ParentRelationItems).CanNavigate);
    }

    [Fact]
    public void FindsTheFullNamespacePathForARelatedType()
    {
        var target = new SymbolExplorerNode("Widget", "class", "C", "", [], SymbolId: "widget-id");
        var typeNamespace = new SymbolExplorerNode("Types", "namespace", "N", "", [target]);
        var rootNamespace = new SymbolExplorerNode("Example", "namespace", "N", "", [typeNamespace]);

        var path = SymbolExplorerNode.FindPathBySymbolId([rootNamespace], "widget-id");

        Assert.Equal([rootNamespace, typeNamespace, target], path);
    }

    private static SymbolExplorerEntry Entry(
        string name,
        string symbolId,
        string kind,
        IReadOnlyList<ExplorerTypeRelation>? parents = null) =>
        new(
            "Example.Types",
            name,
            name,
            kind,
            "Example",
            null,
            name,
            string.Empty,
            [],
            string.Empty,
            symbolId,
            parents ?? []);
}
