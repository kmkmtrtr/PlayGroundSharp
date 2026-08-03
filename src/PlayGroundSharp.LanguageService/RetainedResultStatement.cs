using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PlayGroundSharp.LanguageService;

/// <summary>Creates safe, replayable submissions for retained-result actions.</summary>
public static class RetainedResultStatement
{
    public static bool IsValidVariableName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !string.Equals(name, name.Trim(), StringComparison.Ordinal))
            return false;
        var token = SyntaxFactory.ParseToken(name);
        return token.IsKind(SyntaxKind.IdentifierToken) &&
               !token.ContainsDiagnostics &&
               string.Equals(token.ToFullString(), name, StringComparison.Ordinal);
    }

    public static string Name(int submissionIndex, string typeExpression, string variableName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(submissionIndex);
        if (!IsValidVariableName(variableName))
            throw new ArgumentException("The value is not a valid C# variable name.", nameof(variableName));

        return string.Equals(typeExpression, "dynamic", StringComparison.Ordinal)
            ? $"dynamic {variableName} = RetainResultAsDynamic({submissionIndex});"
            : $"var {variableName} = RetainResultAs<{typeExpression}>({submissionIndex});";
    }

    public static bool RepresentsSameIdentifier(string left, string right)
    {
        if (!IsValidVariableName(left) || !IsValidVariableName(right)) return false;
        return string.Equals(
            SyntaxFactory.ParseToken(left).ValueText,
            SyntaxFactory.ParseToken(right).ValueText,
            StringComparison.Ordinal);
    }

    public static string Release(int submissionIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(submissionIndex);
        return $"ReleaseResult({submissionIndex});";
    }
}
