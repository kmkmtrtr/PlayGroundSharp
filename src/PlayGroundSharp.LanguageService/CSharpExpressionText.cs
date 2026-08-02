using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PlayGroundSharp.LanguageService;

/// <summary>Formats an expression for composition without adding redundant parentheses.</summary>
public static class CSharpExpressionText
{
    public static string Receiver(string expression) => Format(expression, ReceiverMode.MemberAccess);

    public static string CastOperand(string expression) => Format(expression, ReceiverMode.Cast);

    public static string NullForgivenReceiver(string expression)
    {
        var syntax = ParseAndUnwrap(expression);
        if (syntax is null) return $"(({expression.Trim()})!)";
        var text = syntax.ToString();
        return IsPrimaryReceiver(syntax)
            ? $"{text}!"
            : $"(({text})!)";
    }

    private static string Format(string expression, ReceiverMode mode)
    {
        var syntax = ParseAndUnwrap(expression);
        if (syntax is null) return $"({expression.Trim()})";
        var text = syntax.ToString();
        return mode switch
        {
            ReceiverMode.MemberAccess when IsPrimaryReceiver(syntax) => text,
            ReceiverMode.Cast when IsPrimaryReceiver(syntax) => text,
            _ => $"({text})"
        };
    }

    private static ExpressionSyntax? ParseAndUnwrap(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return null;
        ExpressionSyntax syntax = SyntaxFactory.ParseExpression(expression.Trim());
        if (syntax.ContainsDiagnostics) return null;
        while (syntax is ParenthesizedExpressionSyntax parenthesized)
            syntax = parenthesized.Expression;
        return syntax;
    }

    private static bool IsPrimaryReceiver(ExpressionSyntax expression) => expression is
        IdentifierNameSyntax or GenericNameSyntax or
        MemberAccessExpressionSyntax or InvocationExpressionSyntax or ElementAccessExpressionSyntax or
        ConditionalAccessExpressionSyntax or PostfixUnaryExpressionSyntax or
        ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax or
        ArrayCreationExpressionSyntax or ImplicitArrayCreationExpressionSyntax or
        ThisExpressionSyntax or BaseExpressionSyntax or TypeOfExpressionSyntax or
        DefaultExpressionSyntax or SizeOfExpressionSyntax or CheckedExpressionSyntax or TupleExpressionSyntax;

    private enum ReceiverMode
    {
        MemberAccess,
        Cast
    }
}
