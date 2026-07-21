using PlayGroundSharp.Core;

namespace PlayGroundSharp.App.Tests;

public sealed class PackageSearchStateTests
{
    [Fact]
    public async Task WorkspaceActionsReflectSessionActivity()
    {
        await using var viewModel = new MainViewModel { IsSessionChanging = false };
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        Assert.True(viewModel.CanOpenWorkspace);
        Assert.True(viewModel.CanSaveWorkspace);

        viewModel.IsRunning = true;
        Assert.True(viewModel.CanOpenWorkspace);
        Assert.False(viewModel.CanSaveWorkspace);

        viewModel.IsPackageSearchBusy = true;
        Assert.False(viewModel.CanOpenWorkspace);
        Assert.False(viewModel.CanSaveWorkspace);

        viewModel.IsPackageSearchBusy = false;
        viewModel.IsPreparingExecution = true;
        Assert.True(viewModel.CanCancel);
        Assert.False(viewModel.CanOpenWorkspace);
        Assert.False(viewModel.CanSaveWorkspace);
        Assert.Contains(nameof(MainViewModel.CanCancel), changedProperties);
    }

    [Fact]
    public async Task EditingQueryClearsResultsFromPreviousCriteria()
    {
        await using var viewModel = new MainViewModel();
        viewModel.PackageSearchText = "sample";
        viewModel.ApplyPackageSearchResults(CreateResults("sample"));

        Assert.Single(viewModel.PackageSearchItems);

        viewModel.PackageSearchText = "different";

        Assert.Empty(viewModel.PackageSearchItems);
        Assert.Contains("Enter", viewModel.PackageSearchMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingPrereleaseOptionClearsStableOnlyResults()
    {
        await using var viewModel = new MainViewModel();
        viewModel.PackageSearchText = "sample";
        viewModel.ApplyPackageSearchResults(CreateResults("sample"));

        viewModel.IncludePrereleasePackages = true;

        Assert.Empty(viewModel.PackageSearchItems);
        Assert.Contains("Enter", viewModel.PackageSearchMessage, StringComparison.Ordinal);
    }

    private static PackageSearchResultsEvent CreateResults(string query) => new(
        query,
        1,
        [new NuGetPackageInfo("Sample.Package", "1.0.0", "Description", "Author", 100, false)]);
}
