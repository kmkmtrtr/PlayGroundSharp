namespace PlayGroundSharp.App.Tests;

public sealed class ConsoleTabItemTests
{
    [Fact]
    public async Task TabsOwnIndependentSessionViewModels()
    {
        var first = new ConsoleTabItem(1, AppLanguageMode.English);
        var second = new ConsoleTabItem(2, AppLanguageMode.English);
        try
        {
            first.ViewModel.InputText = "var value = 1;";
            first.ViewModel.UsingItems.Add("First.Tab");

            Assert.NotSame(first.ViewModel, second.ViewModel);
            Assert.Empty(second.ViewModel.InputText);
            Assert.DoesNotContain("First.Tab", second.ViewModel.UsingItems);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task TitleFollowsDisplayLanguageWithoutReplacingSession()
    {
        var tab = new ConsoleTabItem(3, AppLanguageMode.Japanese);
        try
        {
            var session = tab.ViewModel;

            Assert.Equal("コンソール 3", tab.Title);
            tab.UpdateLanguage(AppLanguageMode.English);

            Assert.Equal("Console 3", tab.Title);
            Assert.Same(session, tab.ViewModel);
        }
        finally
        {
            await tab.DisposeAsync();
        }
    }

    [Fact]
    public async Task BusyStateTracksBackgroundSessionOperations()
    {
        var tab = new ConsoleTabItem(1, AppLanguageMode.English);
        try
        {
            tab.ViewModel.IsSessionChanging = false;
            var changes = new List<string?>();
            tab.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

            tab.ViewModel.IsRunning = true;

            Assert.True(tab.IsBusy);
            Assert.Contains(nameof(ConsoleTabItem.IsBusy), changes);

            tab.ViewModel.IsRunning = false;

            Assert.False(tab.IsBusy);
        }
        finally
        {
            await tab.DisposeAsync();
        }
    }
}
