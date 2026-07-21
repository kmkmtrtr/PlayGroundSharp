using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private AppLanguageMode languageMode;
    private readonly ResultSnapshot snapshot;
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer notificationTimer = new() { Interval = TimeSpan.FromSeconds(1.8) };
    private SnapshotTreeNode? selectedNode;
    private CancellationTokenSource? searchCancellation;
    private string currentSearchStatus = string.Empty;
    private string appliedQuery = string.Empty;
    private bool copyInProgress;

    public ResultInspectorWindow(ResultSnapshot snapshot, MainViewModel viewModel)
    {
        this.viewModel = viewModel;
        this.snapshot = snapshot;
        languageMode = viewModel.LanguageMode;
        Roots = [SnapshotTreeNode.CreateRoot(snapshot, languageMode)];
        selectedNode = Roots[0];
        InitializeComponent();
        DataContext = this;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
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
            viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            searchTimer.Stop();
            notificationTimer.Stop();
            CancelAndDisposeSearch();
            var bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, ActualWidth, ActualHeight)
                : RestoreBounds;
            viewModel.SaveInspectorLayout(bounds.Width, bounds.Height, SnapshotTreeRow.ActualHeight);
        };
    }

    public ObservableCollection<SnapshotTreeNode> Roots { get; }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.LanguageMode)) return;
        languageMode = viewModel.LanguageMode;
        searchTimer.Stop();
        _ = ApplySearchAsync();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(() => FocusFirstResult(), DispatcherPriority.Input);

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
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            await SaveAllAsync();
            return;
        }
        if (e.Key is Key.Enter or Key.Down && Keyboard.Modifiers == ModifierKeys.None &&
            ReferenceEquals(Keyboard.FocusedElement, SearchBox))
        {
            e.Handled = true;
            searchTimer.Stop();
            if (!string.Equals(appliedQuery, SearchBox.Text, StringComparison.Ordinal))
                await ApplySearchAsync();
            if (!MoveSearchMatch(1)) FocusFirstResult();
            return;
        }
        if (e.Key == Key.F3 &&
            (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift))
        {
            e.Handled = true;
            searchTimer.Stop();
            if (!string.Equals(appliedQuery, SearchBox.Text, StringComparison.Ordinal))
                await ApplySearchAsync();
            MoveSearchMatch(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1);
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
            await CopyToClipboardAsync(() => SnapshotTextFormatter.FormatFull(snapshot));
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox &&
                 selectedNode is not null)
        {
            e.Handled = true;
            await CopyToClipboardAsync(() => selectedNode.CopyText);
        }
    }

    private async Task ApplySearchAsync()
    {
        searchTimer.Stop();
        CancelAndDisposeSearch();
        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        searchCancellation = cancellation;
        var query = SearchBox.Text;
        SetSearchStatus(string.IsNullOrWhiteSpace(query)
            ? string.Empty
            : AppLocalization.Text(languageMode, "Inspector.Searching"));

        SnapshotTreeNode? root;
        int matches;
        int displayedMatches;
        try
        {
            (root, matches, displayedMatches) = await Task.Run(() =>
            {
                var filteredRoot = SnapshotTreeNode.CreateFilteredRoot(
                    snapshot, languageMode, query, out var matchCount, out var displayedMatchCount, cancellationToken);
                return (filteredRoot, matchCount, displayedMatchCount);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception error)
        {
            if (!cancellationToken.IsCancellationRequested) SetSearchStatus(error.Message);
            return;
        }
        finally
        {
            if (ReferenceEquals(searchCancellation, cancellation))
            {
                searchCancellation = null;
                cancellation.Dispose();
            }
        }

        if (cancellationToken.IsCancellationRequested || !string.Equals(query, SearchBox.Text, StringComparison.Ordinal)) return;
        appliedQuery = query;
        Roots.Clear();
        if (root is not null)
        {
            Roots.Add(root);
            SetSelectedNode(root);
        }
        else ClearSelection();
        SetSearchStatus(string.IsNullOrWhiteSpace(query)
            ? string.Empty
            : root is null
                ? AppLocalization.Text(languageMode, "Inspector.NoMatches")
                : matches > displayedMatches
                    ? AppLocalization.Text(languageMode, "Inspector.MatchCountLimited", matches, displayedMatches)
                    : AppLocalization.Text(languageMode, "Inspector.MatchCount", matches));
    }

    private void FocusFirstResult(bool descendToMatch = false)
    {
        if (Roots.Count == 0) return;
        SnapshotTree.UpdateLayout();
        if (SnapshotTree.ItemContainerGenerator.ContainerFromIndex(0) is not TreeViewItem item) return;
        while (descendToMatch && item.HasItems)
        {
            item.IsExpanded = true;
            item.UpdateLayout();
            if (item.ItemContainerGenerator.ContainerFromIndex(0) is not TreeViewItem child) break;
            item = child;
        }
        item.IsSelected = true;
        item.BringIntoView();
        item.Focus();
    }

    private bool MoveSearchMatch(int direction)
    {
        if (string.IsNullOrWhiteSpace(appliedQuery)) return false;
        var matches = new List<(SnapshotTreeNode Node, IReadOnlyList<SnapshotTreeNode> Ancestors)>();
        foreach (var root in Roots) CollectSearchMatches(root, [], matches);
        if (matches.Count == 0) return false;

        var currentIndex = matches.FindIndex(match => ReferenceEquals(match.Node, selectedNode));
        var nextIndex = currentIndex < 0
            ? direction > 0 ? 0 : matches.Count - 1
            : (currentIndex + direction + matches.Count) % matches.Count;
        var target = matches[nextIndex];
        foreach (var ancestor in target.Ancestors) ancestor.IsExpanded = true;
        if (selectedNode is not null && !ReferenceEquals(selectedNode, target.Node)) selectedNode.IsSelected = false;
        target.Node.IsSelected = true;
        SetSelectedNode(target.Node);
        Dispatcher.BeginInvoke(() => FocusNode(target.Node), DispatcherPriority.Input);
        return true;
    }

    private static void CollectSearchMatches(
        SnapshotTreeNode node,
        IReadOnlyList<SnapshotTreeNode> ancestors,
        ICollection<(SnapshotTreeNode Node, IReadOnlyList<SnapshotTreeNode> Ancestors)> matches)
    {
        if (node.IsSearchMatch) matches.Add((node, ancestors));
        var childAncestors = ancestors.Append(node).ToArray();
        foreach (var child in node.Children) CollectSearchMatches(child, childAncestors, matches);
    }

    private void FocusNode(SnapshotTreeNode node)
    {
        SnapshotTree.UpdateLayout();
        if (FindContainer(SnapshotTree, node) is not { } item) return;
        item.IsSelected = true;
        item.BringIntoView();
        item.Focus();
    }

    private static TreeViewItem? FindContainer(ItemsControl parent, SnapshotTreeNode node)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem direct) return direct;
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container ||
                !container.IsExpanded) continue;
            container.UpdateLayout();
            if (FindContainer(container, node) is { } descendant) return descendant;
        }
        return null;
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
        await CopyToClipboardAsync(() => selectedNode.CopyText);
    }

    private async void CopyAll_Click(object sender, RoutedEventArgs e) =>
        await CopyToClipboardAsync(() => SnapshotTextFormatter.FormatFull(snapshot));

    private async void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (selectedNode is not null) await CopyToClipboardAsync(() => selectedNode.Path);
    }

    private async void SaveAll_Click(object sender, RoutedEventArgs e) => await SaveAllAsync();

    private async Task SaveAllAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = AppLocalization.Text(languageMode, "Dialog.ResultSaveTitle"),
            Filter = AppLocalization.Text(languageMode, "Dialog.ResultFileFilter"),
            FilterIndex = 2,
            DefaultExt = ".json",
            AddExtension = true,
            FileName = $"PlayGroundSharp-result-{DateTime.Now:yyyyMMdd-HHmmss}.json"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            SetSearchStatus(AppLocalization.Text(languageMode, "Status.SavingResult"));
            var text = await Task.Run(() =>
                Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase)
                    ? SnapshotJsonFormatter.Format(snapshot)
                    : SnapshotTextFormatter.FormatFull(snapshot));
            await File.WriteAllTextAsync(dialog.FileName, text);
            ShowNotification("Status.Saved", Path.GetFileName(dialog.FileName));
        }
        catch (Exception error)
        {
            SetSearchStatus(AppLocalization.Text(languageMode, "Status.SaveFailed"));
            MessageBox.Show(this, error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetSelectedNode(SnapshotTreeNode node)
    {
        selectedNode = node;
        UpdateSelectionActions(true);
        DetailText.Text = node.Detail;
        PathText.Text = node.Path;
    }

    private void ClearSelection()
    {
        if (selectedNode is not null) selectedNode.IsSelected = false;
        selectedNode = null;
        UpdateSelectionActions(false);
        DetailText.Clear();
        PathText.Text = string.Empty;
    }

    private void CancelAndDisposeSearch()
    {
        var cancellation = searchCancellation;
        searchCancellation = null;
        if (cancellation is null) return;
        try
        {
            cancellation.Cancel();
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void UpdateSelectionActions(bool enabled)
    {
        ExpandSelectedButton.IsEnabled = enabled;
        CollapseSelectedButton.IsEnabled = enabled;
        CopySelectedButton.IsEnabled = enabled;
        CopyPathButton.IsEnabled = enabled;
    }

    private async Task CopyToClipboardAsync(Func<string> textFactory)
    {
        if (copyInProgress) return;
        copyInProgress = true;
        SetSearchStatus(AppLocalization.Text(languageMode, "Status.Copying"));
        try
        {
            await ClipboardService.SetTextAsync(await Task.Run(textFactory));
            ShowNotification("Status.Copied");
        }
        catch (Exception error)
        {
            SetSearchStatus(AppLocalization.Text(languageMode, "Status.CopyFailed"));
            MessageBox.Show(this, error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            copyInProgress = false;
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
