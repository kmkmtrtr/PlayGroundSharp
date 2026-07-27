using PlayGroundSharp.LanguageService;

namespace PlayGroundSharp.App.Tests;

public sealed class DocumentationPresentationTests
{
    [Fact]
    public void ParsesStructuredQuickInfoSections()
    {
        var text = string.Join(
            Environment.NewLine + Environment.NewLine,
            "IEnumerable<int> Enumerable.Range(int start, int count)",
            "Generates a sequence.",
            "▶ start : int" + Environment.NewLine + "  The first integer.",
            "• count : int" + Environment.NewLine + "  The number of integers.",
            "→ A sequence of integers.");

        var presentation = DocumentationPresentationParser.Parse(text);

        Assert.Equal("IEnumerable<int> Enumerable.Range(int start, int count)", presentation.Signature);
        Assert.Equal("Generates a sequence.", presentation.Summary);
        Assert.Collection(
            presentation.Parameters,
            parameter =>
            {
                Assert.True(parameter.IsActive);
                Assert.Equal("start", parameter.Name);
                Assert.Equal("int", parameter.TypeName);
                Assert.Equal("The first integer.", parameter.Summary);
            },
            parameter =>
            {
                Assert.False(parameter.IsActive);
                Assert.Equal("count", parameter.Name);
            });
        Assert.Equal("A sequence of integers.", presentation.Returns);
    }

    [Fact]
    public void SeparatesSingleLineSignatureFromFollowingSummary()
    {
        const string text = "bool string.Contains(string value)\n" +
                            "Determines whether the specified value occurs within this string.";

        var presentation = DocumentationPresentationParser.Parse(text);

        Assert.Equal("bool string.Contains(string value)", presentation.Signature);
        Assert.Equal(
            "Determines whether the specified value occurs within this string.",
            presentation.Summary);
    }

    [Fact]
    public void RecognizesPropertyQuickInfoAsASignature()
    {
        const string text = "int string.Length { get; }\nGets the number of characters.";

        var presentation = DocumentationPresentationParser.Parse(text);

        Assert.Equal("int string.Length { get; }", presentation.Signature);
        Assert.Equal("Gets the number of characters.", presentation.Summary);
    }

    [Theory]
    [InlineData("Method", "M")]
    [InlineData("Property", "P")]
    [InlineData("Class", "C")]
    [InlineData("Interface", "I")]
    [InlineData("Namespace", "N")]
    public void ProvidesCompactCompletionKindGlyph(string tag, string expected)
    {
        var candidate = new CompletionCandidate("Item", "Item", "Item", [tag]);

        Assert.Equal(expected, candidate.KindGlyph);
    }
}
