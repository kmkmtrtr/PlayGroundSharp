using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PlayGroundSharp.App;

public sealed partial class ConsoleTabItem : ObservableObject, IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private Task? initialization;
    private bool disposed;

    public ConsoleTabItem(int number, AppLanguageMode languageMode, MainViewModel? viewModel = null)
    {
        Number = number;
        ViewModel = viewModel ?? new MainViewModel();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateLanguage(languageMode);
    }

    public int Number { get; }
    public MainViewModel ViewModel { get; }

    [ObservableProperty]
    private string title = string.Empty;

    public bool IsBusy =>
        ViewModel.IsRunning || ViewModel.IsPreparingExecution || ViewModel.IsPackageSearchBusy ||
        ViewModel.IsWorkspaceBusy || ViewModel.IsSessionChanging;

    public int EditorCaretOffset { get; set; }
    public int EditorSelectionStart { get; set; }
    public int EditorSelectionLength { get; set; }
    public double TranscriptVerticalOffset { get; set; }
    public bool TranscriptAutoScroll { get; set; } = true;
    public bool HasCapturedUiState { get; set; }

    public void UpdateLanguage(AppLanguageMode languageMode) =>
        Title = AppLocalization.Text(languageMode, "ConsoleTab.Title", Number);

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsRunning) or
            nameof(MainViewModel.IsPreparingExecution) or
            nameof(MainViewModel.IsPackageSearchBusy) or
            nameof(MainViewModel.IsWorkspaceBusy) or
            nameof(MainViewModel.IsSessionChanging))
            OnPropertyChanged(nameof(IsBusy));
    }

    public Task InitializeAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return initialization ??= ViewModel.InitializeAsync(lifetime.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        lifetime.Cancel();
        if (initialization is not null)
        {
            try
            {
                await initialization;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
        try
        {
            await ViewModel.DisposeAsync();
        }
        finally
        {
            lifetime.Dispose();
        }
    }
}
