using PlayGroundSharp.Core;

namespace PlayGroundSharp.App.Tests;

public sealed class DiagnosticNavigationTests
{
    [Fact]
    public async Task DiagnosticsAreSortedNavigatedAndInvalidatedByEditing()
    {
        await using var viewModel = new MainViewModel();
        const string code = "first + second";
        viewModel.InputText = code;
        var later = new DiagnosticInfo("CS0002", DiagnosticLevel.Warning, "later", 1, 9, 1, 15);
        var earlier = new DiagnosticInfo("CS0001", DiagnosticLevel.Error, "earlier", 1, 1, 1, 6);

        Assert.True(viewModel.ApplyDiagnostics(code, [later, earlier]));
        Assert.True(viewModel.HasNavigableDiagnostics);
        Assert.Equal(earlier, viewModel.MoveDiagnostic(1));
        Assert.Equal(later, viewModel.MoveDiagnostic(1));
        Assert.Equal(earlier, viewModel.MoveDiagnostic(1));
        Assert.Equal(later, viewModel.MoveDiagnostic(-1));

        viewModel.InputText = "edited";

        Assert.False(viewModel.HasNavigableDiagnostics);
        Assert.Null(viewModel.MoveDiagnostic(1));
    }

    [Fact]
    public async Task DiagnosticsFromStaleInputAreIgnored()
    {
        await using var viewModel = new MainViewModel { InputText = "current" };
        var diagnostic = new DiagnosticInfo("CS0001", DiagnosticLevel.Error, "stale", 1, 1, 1, 2);

        Assert.False(viewModel.ApplyDiagnostics("previous", [diagnostic]));
        Assert.False(viewModel.HasNavigableDiagnostics);
    }
}
