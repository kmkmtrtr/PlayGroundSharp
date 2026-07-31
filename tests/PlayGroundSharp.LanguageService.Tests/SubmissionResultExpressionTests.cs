using PlayGroundSharp.LanguageService;

namespace PlayGroundSharp.LanguageService.Tests;

public sealed class SubmissionResultExpressionTests
{
    [Theory]
    [InlineData("customers", "customers")]
    [InlineData("var customers = GetCustomers();\ncustomers.Where(x => x.Active)", "customers.Where(x => x.Active)")]
    [InlineData("await LoadAsync()", "await LoadAsync()")]
    public void ExtractsFinalScriptExpression(string code, string expected) =>
        Assert.Equal(expected, SubmissionResultExpression.TryExtract(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("var customers = GetCustomers();")]
    [InlineData("customers.")]
    public void ReturnsNullWithoutAValidResultExpression(string? code) =>
        Assert.Null(SubmissionResultExpression.TryExtract(code));
}
