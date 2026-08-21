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
        var diagnosticChanges = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.CurrentDiagnostics)) diagnosticChanges++;
        };

        Assert.True(viewModel.ApplyDiagnostics(code, [later, earlier]));
        Assert.Equal(1, diagnosticChanges);
        Assert.True(viewModel.HasNavigableDiagnostics);
        Assert.Equal([earlier, later], viewModel.CurrentDiagnostics);
        Assert.Equal(earlier, viewModel.MoveDiagnostic(1));
        Assert.Equal(later, viewModel.MoveDiagnostic(1));
        Assert.Equal(earlier, viewModel.MoveDiagnostic(1));
        Assert.Equal(later, viewModel.MoveDiagnostic(-1));

        viewModel.InputText = "edited";

        Assert.False(viewModel.HasNavigableDiagnostics);
        Assert.Empty(viewModel.CurrentDiagnostics);
        Assert.Equal(2, diagnosticChanges);
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

    [Fact]
    public async Task CommandInputIsNotAnalyzedAsCSharp()
    {
        await using var viewModel = new MainViewModel { InputText = ":using add System.Net.Http" };

        for (var attempt = 0; attempt < 50 &&
             viewModel.CurrentDiagnostics.Count == 0 &&
             !viewModel.DiagnosticStatus.Contains('0'); attempt++)
            await Task.Delay(100);

        Assert.Empty(viewModel.CurrentDiagnostics);
        Assert.Contains('0', viewModel.DiagnosticStatus);
        Assert.Null(await viewModel.GetQuickInfoAsync(viewModel.InputText.Length));
        Assert.Null(await viewModel.GetSignatureHelpAsync(viewModel.InputText.Length));
    }
}
