using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PlayGroundSharp.LanguageService;

/// <summary>Finds the expression whose value is returned by a C# script submission.</summary>
public static class SubmissionResultExpression
{
    public static string ForResult(string? code, int submissionIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(submissionIndex);
        return TryExtract(code) ?? $"Out[{submissionIndex}]";
    }

    public static string? TryExtract(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        var tree = CSharpSyntaxTree.ParseText(
            code,
            new CSharpParseOptions(LanguageVersion.Latest, kind: SourceCodeKind.Script));
        if (tree.GetDiagnostics().Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            return null;

        var root = tree.GetCompilationUnitRoot();
        if (root.Members.LastOrDefault() is not GlobalStatementSyntax
            {
                Statement: ExpressionStatementSyntax statement
            })
            return null;

        var expression = statement.Expression.ToFullString().Trim();
        return expression.Length == 0 ? null : expression;
    }
}
