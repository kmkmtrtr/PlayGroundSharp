using PlayGroundSharp.Core;
using PlayGroundSharp.LanguageService;

namespace PlayGroundSharp.LanguageService.Tests;

public sealed class CSharpExpressionTextTests
{
    [Theory]
    [InlineData("customers", "customers")]
    [InlineData("(customers)", "customers")]
    [InlineData("GetCustomers()", "GetCustomers()")]
    [InlineData("customers.Where(x => x.Active)", "customers.Where(x => x.Active)")]
    [InlineData("first ?? second", "(first ?? second)")]
    [InlineData("condition ? first : second", "(condition ? first : second)")]
    public void ReceiverKeepsOnlyRequiredParentheses(string expression, string expected) =>
        Assert.Equal(expected, CSharpExpressionText.Receiver(expression));

    [Theory]
    [InlineData("json", "json!")]
    [InlineData("(json)", "json!")]
    [InlineData("GetJson()", "GetJson()!")]
    [InlineData("first ?? second", "((first ?? second)!)")]
    public void NullForgivenReceiverKeepsTheOperatorOnTheWholeExpression(
        string expression,
        string expected) =>
        Assert.Equal(expected, CSharpExpressionText.NullForgivenReceiver(expression));

    [Fact]
    public async Task RequiredParenthesesProduceValidComposedExpressions()
    {
        var context = SessionContext.Empty with
        {
            Submissions =
            [
                "int[]? first = null;",
                "int[] second = [1, 2];",
                "JsonNode? firstJson = null;",
                "JsonNode? secondJson = JsonNode.Parse(\"[]\");"
            ]
        };
        var service = new CSharpLanguageService();
        var sequence = $"{CSharpExpressionText.Receiver("first ?? second")}.Select(value => value)";
        var json = $"{CSharpExpressionText.NullForgivenReceiver("firstJson ?? secondJson")}.AsArray()";

        Assert.DoesNotContain(
            await service.GetDiagnosticsAsync(context, sequence),
            static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.DoesNotContain(
            await service.GetDiagnosticsAsync(context, json),
            static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }
}
