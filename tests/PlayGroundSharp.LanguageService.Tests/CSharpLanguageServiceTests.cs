using PlayGroundSharp.Core;
using PlayGroundSharp.LanguageService;

namespace PlayGroundSharp.LanguageService.Tests;

public sealed class CSharpLanguageServiceTests
{
    private readonly CSharpLanguageService service = new();

    [Fact]
    public async Task CompletesSessionVariablesArrayAndLinqMembers()
    {
        var context = new SessionContext(["var values = Enumerable.Range(1, 10).ToArray();"], SessionContext.DefaultImports, []);
        var items = await service.GetCompletionsAsync(context, "values.", "values.".Length);
        var names = items.Select(static item => item.DisplayText).ToHashSet(StringComparer.Ordinal);
        var diagnostics = await service.GetDiagnosticsAsync(context, "values.");

        Assert.True(names.Contains("Length"), string.Join(" | ", diagnostics.Select(static item => $"{item.Id}: {item.Message}")));
        Assert.Contains("Where", names);
        Assert.Contains("Select", names);
        Assert.Contains("Sum", names);
        Assert.Contains("ToArray", names);
    }

    [Fact]
    public async Task CompletesDefinedType()
    {
        var context = new SessionContext(["record User(string Name, int Age);"], SessionContext.DefaultImports, []);
        var items = await service.GetCompletionsAsync(context, "new Us", "new Us".Length);
        Assert.Contains(items, static item => item.DisplayText == "User");
    }

    [Fact]
    public async Task ReturnsSignatureHelpAndDiagnostics()
    {
        var signature = await service.GetSignatureHelpAsync(SessionContext.Empty, "string.Join(", "string.Join(".Length);
        var diagnostics = await service.GetDiagnosticsAsync(SessionContext.Empty, "unknownName + 1");

        Assert.NotNull(signature);
        Assert.Contains(signature.Signatures, static text => text.Contains("Join", StringComparison.Ordinal));
        Assert.Contains(diagnostics, static diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }
}
