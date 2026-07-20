using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

public partial class ResultInspectorWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly AppLanguageMode languageMode;
    private readonly ResultSnapshot snapshot;
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer notificationTimer = new() { Interval = TimeSpan.FromSeconds(1.8) };
    private SnapshotTreeNode? selectedNode;
    private CancellationTokenSource? searchCancellation;
    private string currentSearchStatus = string.Empty;

    public ResultInspectorWindow(ResultSnapshot snapshot, MainViewModel viewModel)
    {
        this.viewModel = viewModel;
        this.snapshot = snapshot;
        languageMode = viewModel.LanguageMode;
        Roots = [SnapshotTreeNode.CreateRoot(snapshot, languageMode)];
        selectedNode = Roots[0];
        InitializeComponent();
        DataContext = this;
        var settings = viewModel.SavedSettings;
        Width = settings.InspectorWidth;
        Height = settings.InspectorHeight;
        SnapshotTreeRow.Height = new(Math.Min(
            settings.InspectorTreeHeight,
            Math.Max(120, settings.InspectorHeight - 180)));
        SetSelectedNode(Roots[0]);
        searchTimer.Tick += async (_, _) => await ApplySearchAsync();
        notificationTimer.Tick += (_, _) =>
        {
            notificationTimer.Stop();
            SearchStatus.Text = currentSearchStatus;
        };
        Closed += (_, _) =>
        {
            searchTimer.Stop();
            notificationTimer.Stop();
            searchCancellation?.Cancel();
            searchCancellation?.Dispose();
            var bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, ActualWidth, ActualHeight)
                : RestoreBounds;
            viewModel.SaveInspectorLayout(bounds.Width, bounds.Height, SnapshotTreeRow.ActualHeight);
        };
    }

    public ObservableCollection<SnapshotTreeNode> Roots { get; }

    private void SnapshotTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not SnapshotTreeNode node) return;
        SetSelectedNode(node);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        searchCancellation?.Cancel();
        searchTimer.Stop();
        searchTimer.Start();
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
            return;
        }
        if (e.Key == Key.Enter && ReferenceEquals(Keyboard.FocusedElement, SearchBox))
        {
            e.Handled = true;
            searchTimer.Stop();
            await ApplySearchAsync();
            return;
        }
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (SearchBox.Text.Length > 0)
            {
                SearchBox.Clear();
                searchTimer.Stop();
                await ApplySearchAsync();
            }
            else
                Close();
            return;
        }
        if (e.Key != Key.C) return;
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            CopyToClipboard(await Task.Run(() => SnapshotTextFormatter.FormatFull(snapshot)));
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox &&
                 selectedNode is not null)
        {
            e.Handled = true;
            CopyToClipboard(await Task.Run(() => selectedNode.CopyText));
        }
    }

    private async Task ApplySearchAsync()
    {
        searchTimer.Stop();
        searchCancellation?.Cancel();
        searchCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        searchCancellation = cancellation;
        var query = SearchBox.Text;
        SetSearchStatus(string.IsNullOrWhiteSpace(query)
            ? string.Empty
            : AppLocalization.Text(languageMode, "Inspector.Searching"));

        SnapshotTreeNode? root;
        int matches;
        try
        {
            (root, matches) = await Task.Run(() =>
            {
                var filteredRoot = SnapshotTreeNode.CreateFilteredRoot(
                    snapshot, languageMode, query, out var matchCount, cancellationToken);
                return (filteredRoot, matchCount);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested || !string.Equals(query, SearchBox.Text, StringComparison.Ordinal)) return;
        Roots.Clear();
        if (root is not null)
        {
            Roots.Add(root);
            SetSelectedNode(root);
        }
        else
        {
            selectedNode = null;
            DetailText.Clear();
            PathText.Text = string.Empty;
        }
        SetSearchStatus(string.IsNullOrWhiteSpace(query)
            ? string.Empty
            : root is null
                ? AppLocalization.Text(languageMode, "Inspector.NoMatches")
                : AppLocalization.Text(languageMode, "Inspector.MatchCount", matches));
    }

    private void ExpandSelected_Click(object sender, RoutedEventArgs e)
    {
        if (selectedNode is not null) selectedNode.IsExpanded = true;
    }

    private void CollapseSelected_Click(object sender, RoutedEventArgs e)
    {
        if (selectedNode is not null) selectedNode.IsExpanded = false;
    }

    private async void CopySelected_Click(object sender, RoutedEventArgs e)
    {
        if (selectedNode is null) return;
        CopyToClipboard(await Task.Run(() => selectedNode.CopyText));
    }

    private async void CopyAll_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard(await Task.Run(() => SnapshotTextFormatter.FormatFull(snapshot)));

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (selectedNode is not null) CopyToClipboard(selectedNode.Path);
    }

    private async void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = AppLocalization.Text(languageMode, "Dialog.ResultSaveTitle"),
            Filter = AppLocalization.Text(languageMode, "Dialog.ResultFileFilter"),
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = $"PlayGroundSharp-result-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var text = await Task.Run(() =>
                Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase)
                    ? SnapshotJsonFormatter.Format(snapshot)
                    : SnapshotTextFormatter.FormatFull(snapshot));
            await File.WriteAllTextAsync(dialog.FileName, text);
            ShowNotification("Status.Saved", Path.GetFileName(dialog.FileName));
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetSelectedNode(SnapshotTreeNode node)
    {
        selectedNode = node;
        DetailText.Text = node.Detail;
        PathText.Text = node.Path;
    }

    private void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            ShowNotification("Status.Copied");
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetSearchStatus(string text)
    {
        notificationTimer.Stop();
        currentSearchStatus = text;
        SearchStatus.Text = text;
    }

    private void ShowNotification(string key, params object?[] arguments)
    {
        notificationTimer.Stop();
        SearchStatus.Text = AppLocalization.Text(languageMode, key, arguments);
        notificationTimer.Start();
    }
}
