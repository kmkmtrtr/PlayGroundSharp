using PlayGroundSharp.LanguageService;

namespace PlayGroundSharp.App.Tests;

public sealed class CompletionPlacementTests
{
    [Fact]
    public void UsesExplicitInsertionPointForTrailingExpressionMembers()
    {
        CompletionCandidate[] candidates =
        [
            new("Add", "Add", "Add", ["Method"], ".Add", ReplacementStart: 11),
            new("Where", "Where", "Where", ["ExtensionMethod"], ".Where", ReplacementStart: 11)
        ];

        Assert.Equal(11, CompletionPlacement.FindStart("typedResult", 11, candidates));
    }

    [Fact]
    public void UsesTypedIdentifierForOrdinaryCompletion()
    {
        CompletionCandidate[] candidates =
        [
            new("WriteLine", "WriteLine", "WriteLine", ["Method"])
        ];

        Assert.Equal(8, CompletionPlacement.FindStart("Console.Wri", 11, candidates));
    }

    [Fact]
    public void KeepsTheFilterAfterTheDotForNumericLiteralReplacement()
    {
        CompletionCandidate[] candidates =
        [
            new("Billions", "Billions", "Billions", ["ExtensionMethod"],
                "(1).Billions", ReplacementStart: 0)
        ];

        Assert.Equal(2, CompletionPlacement.FindStart("1.", 2, candidates));
    }
}
