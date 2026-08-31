using PlayGroundSharp.LanguageService;

namespace PlayGroundSharp.App;

internal static class CompletionPlacement
{
    public static int FindStart(
        string text,
        int caretOffset,
        IReadOnlyList<CompletionCandidate> candidates)
    {
        var caret = Math.Clamp(caretOffset, 0, text.Length);
        if (candidates.Count > 0 &&
            candidates[0].ReplacementStart is { } explicitStart &&
            explicitStart == caret &&
            candidates.All(candidate => candidate.ReplacementStart == explicitStart))
            return caret;

        var start = caret;
        while (start > 0 && IsIdentifierPart(text[start - 1])) start--;
        return start;
    }

    private static bool IsIdentifierPart(char character) =>
        char.IsLetterOrDigit(character) || character == '_';
}
