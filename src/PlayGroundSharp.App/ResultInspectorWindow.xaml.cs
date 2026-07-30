using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

public partial class ResultInspectorWindow : Window
{
    private const int WmMouseHorizontalWheel = 0x020E;
    private readonly MainViewModel viewModel;
    private AppLanguageMode languageMode;
    private readonly ResultSnapshot snapshot;
    private readonly Dictionary<DataGridColumn, int> tableColumnIndexes = [];
    private readonly Dictionary<DataGridColumn, TableSortState> tableSortStates = [];
    private readonly Dictionary<DataGridColumn, TextBlock> tableSortGlyphs = [];
    private readonly Dictionary<DataGridColumn, FrameworkElement> tableSortHeaders = [];
    private readonly Stack<TableNavigationState> tableHistory = [];
    private SnapshotTableModel? tableModel;
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer notificationTimer = new() { Interval = TimeSpan.FromSeconds(1.8) };
    private readonly DispatcherTimer tableCacheWarmupTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(50)
    };
    private SnapshotTreeNode? selectedNode;
    private CancellationTokenSource? searchCancellation;
    private string currentSearchStatus = string.Empty;
    private string tableSummaryStatus = string.Empty;
    private string tablePath = "$";
    private string appliedQuery = string.Empty;
    private bool copyInProgress;
    private bool isTableMode;
    private bool isConfiguringTable;
    private int currentTableCachedRowCount;
    private int targetTableCachedRowCount;
    private ScrollViewer? tableScrollViewer;
    private HwndSource? windowSource;

    public ResultInspectorWindow(ResultSnapshot snapshot, MainViewModel viewModel)
    {
        this.viewModel = viewModel;
        this.snapshot = snapshot;
        tableModel = SnapshotTableModel.TryCreate(snapshot);
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
        ConfigureTable();
        SetSelectedNode(Roots[0]);
        SetTableMode(tableModel?.PreferTableView == true);
        searchTimer.Tick += async (_, _) => await ApplySearchAsync();
        notificationTimer.Tick += (_, _) =>
        {
            notificationTimer.Stop();
            RestoreStatusDisplays();
        };
        tableCacheWarmupTimer.Tick += TableCacheWarmupTimer_Tick;
        Closed += (_, _) =>
        {
            windowSource?.RemoveHook(WindowMessageHook);
            windowSource = null;
            viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            searchTimer.Stop();
            notificationTimer.Stop();
            tableCacheWarmupTimer.Stop();
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
        UpdateTableSummary();
        foreach (var column in tableSortStates.Keys)
            UpdateTableSortHeader(column);
        searchTimer.Stop();
        _ = ApplySearchAsync();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        windowSource?.AddHook(WindowMessageHook);
        tableScrollViewer = ScrollWheelRouter.FindScrollViewer(TableGrid);
        StartTableCacheWarmup();
        Dispatcher.BeginInvoke(
            () =>
            {
                if (isTableMode) TableGrid.Focus();
                else FocusFirstResult();
            },
            DispatcherPriority.Input);
    }

    private void SnapshotTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not SnapshotTreeNode node) return;
        SetSelectedNode(node);
    }

    private void SnapshotTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            FindAncestor<ToggleButton>(e.OriginalSource as DependencyObject) is not null ||
            FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is not null)
            return;

        var point = e.GetPosition(SnapshotTree);
        if (point.X < 0 || point.Y < 0 ||
            point.X >= SnapshotTree.ActualWidth - SystemParameters.VerticalScrollBarWidth ||
            point.Y > SnapshotTree.ActualHeight)
            return;

        var item = FindTreeItemAtRow(point.Y);
        if (item is null) return;
        item.IsSelected = true;
        item.Focus();
        e.Handled = true;
    }

    private TreeViewItem? FindTreeItemAtRow(double y)
    {
        var scanWidth = Math.Max(
            0,
            SnapshotTree.ActualWidth - SystemParameters.VerticalScrollBarWidth);
        for (var x = 2d; x < scanWidth; x += 8)
        {
            var hit = SnapshotTree.InputHitTest(new Point(x, y)) as DependencyObject;
            if (FindAncestor<TreeViewItem>(hit) is { } item) return item;
        }
        return null;
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
            SetTableMode(false);
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
            if (isTableMode) SetTableMode(false);
            searchTimer.Stop();
            if (!string.Equals(appliedQuery, SearchBox.Text, StringComparison.Ordinal))
                await ApplySearchAsync();
            MoveSearchMatch(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1);
            return;
        }
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (isTableMode)
            {
                if (NavigateToParentTable()) return;
                Close();
                return;
            }
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
        else if (!isTableMode && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox &&
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

    private void TreeMode_Click(object sender, RoutedEventArgs e) => SetTableMode(false);

    private void TableMode_Click(object sender, RoutedEventArgs e)
    {
        if (isTableMode || selectedNode is null) return;
        var selectedTable = SnapshotTableModel.TryCreate(selectedNode.Snapshot);
        if (selectedTable is null) return;
        tableHistory.Clear();
        ShowTable(selectedTable, selectedNode.Path);
        SetTableMode(true);
    }

    private void TableBack_Click(object sender, RoutedEventArgs e) => NavigateToParentTable();

    private void OpenCellTable_Click(object sender, RoutedEventArgs e) => OpenSelectedCellTable();

    private async void CopyTable_Click(object sender, RoutedEventArgs e)
    {
        if (tableModel is not null)
            await CopyToClipboardAsync(() => tableModel.FormatDelimited('\t'));
    }

    private async void SaveTable_Click(object sender, RoutedEventArgs e) => await SaveTableAsync();

    private void TableGrid_LoadingRow(object sender, DataGridRowEventArgs e) =>
        e.Row.Header = (e.Row.GetIndex() + 1).ToString("N0");

    private void TableGrid_SelectedCellsChanged(object sender, SelectedCellsChangedEventArgs e)
    {
        if (!isConfiguringTable) UpdateOpenCellTableState();
    }

    private void TableGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var model = tableModel;
        if (model is null ||
            TableGrid.ItemsSource is null ||
            !tableSortStates.TryGetValue(e.Column, out var currentState) ||
            !tableColumnIndexes.TryGetValue(e.Column, out var columnIndex) ||
            CollectionViewSource.GetDefaultView(TableGrid.ItemsSource) is not ListCollectionView view)
            return;

        var nextState = TableSortCycle.Next(currentState);
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.CustomSort = null;
            foreach (var column in tableSortStates.Keys.ToArray())
            {
                tableSortStates[column] = TableSortState.Original;
                column.SortDirection = null;
                UpdateTableSortHeader(column);
            }

            if (TableSortCycle.ToListSortDirection(nextState) is { } direction)
            {
                view.CustomSort = SnapshotTableRowComparer.Create(
                    model.Rows,
                    columnIndex,
                    direction);
                tableSortStates[e.Column] = nextState;
                e.Column.SortDirection = direction;
                UpdateTableSortHeader(e.Column);
            }
        }
    }

    private void TableGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject) is null) return;
        if (OpenSelectedCellTable()) e.Handled = true;
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || !isTableMode || !TableGrid.IsMouseOver ||
            tableScrollViewer is null)
            return;
        var forceHorizontal = ScrollWheelRouter.IsOverHorizontalScrollZone(
            TableGrid,
            e.GetPosition(TableGrid));
        if (ScrollWheelRouter.TryRouteHorizontalWheel(
                tableScrollViewer,
                e.OriginalSource as DependencyObject,
                e.Delta,
                Keyboard.Modifiers,
                forceHorizontal))
            e.Handled = true;
    }

    private IntPtr WindowMessageHook(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        if (message != WmMouseHorizontalWheel || !isTableMode ||
            tableScrollViewer is null)
            return IntPtr.Zero;

        var delta = unchecked((short)((wordParameter.ToInt64() >> 16) & 0xffff));
        var screenX = unchecked((short)(longParameter.ToInt64() & 0xffff));
        var screenY = unchecked((short)((longParameter.ToInt64() >> 16) & 0xffff));
        var point = TableGrid.PointFromScreen(new Point(screenX, screenY));
        if (point.X < 0 || point.Y < 0 ||
            point.X > TableGrid.ActualWidth || point.Y > TableGrid.ActualHeight)
            return IntPtr.Zero;

        if (ScrollWheelRouter.TryRouteHorizontalWheel(
                tableScrollViewer,
                null,
                delta,
                ModifierKeys.None,
                forceHorizontal: true))
            handled = true;
        return IntPtr.Zero;
    }

    private void TableGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Enter)
        {
            if (OpenSelectedCellTable()) e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Back) ||
            (Keyboard.Modifiers == ModifierKeys.Alt &&
             (e.Key == Key.Left || e.Key == Key.System && e.SystemKey == Key.Left)))
        {
            if (NavigateToParentTable()) e.Handled = true;
        }
    }

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

    private async Task SaveTableAsync()
    {
        if (tableModel is null) return;
        var dialog = new SaveFileDialog
        {
            Title = AppLocalization.Text(languageMode, "Dialog.TableSaveTitle"),
            Filter = AppLocalization.Text(languageMode, "Dialog.CsvFileFilter"),
            DefaultExt = ".csv",
            AddExtension = true,
            FileName = $"PlayGroundSharp-table-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            SetSearchStatus(AppLocalization.Text(languageMode, "Status.SavingResult"));
            var text = await Task.Run(() => tableModel.FormatDelimited(','));
            await File.WriteAllTextAsync(dialog.FileName, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            ShowNotification("Status.Saved", Path.GetFileName(dialog.FileName));
        }
        catch (Exception error)
        {
            SetSearchStatus(AppLocalization.Text(languageMode, "Status.SaveFailed"));
            MessageBox.Show(this, error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowTable(SnapshotTableModel model, string path)
    {
        tableModel = model;
        tablePath = path;
        ConfigureTable();
    }

    private bool OpenSelectedCellTable()
    {
        if (!TryGetSelectedTableCell(
                out var cell,
                out var path,
                out var columnIndex,
                out var sourceIndex) ||
            cell.Source is null ||
            tableModel is null)
            return false;

        var nestedTable = SnapshotTableModel.TryCreate(cell.Source);
        if (nestedTable is null) return false;

        tableHistory.Push(new(tableModel, tablePath, sourceIndex, columnIndex));
        ShowTable(nestedTable, path);
        Dispatcher.BeginInvoke(() => TableGrid.Focus(), DispatcherPriority.Input);
        return true;
    }

    private bool NavigateToParentTable()
    {
        if (!tableHistory.TryPop(out var parent)) return false;
        ShowTable(parent.Model, parent.Path);
        RestoreTableSelection(parent.SelectedSourceIndex, parent.SelectedColumnIndex);
        return true;
    }

    private void TableColumnHeader_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not DataGridColumnHeader { Column: { } column, ContextMenu: { } menu } ||
            !tableColumnIndexes.TryGetValue(column, out var columnIndex) ||
            menu.Items.OfType<MenuItem>().FirstOrDefault() is not { } flattenItem)
        {
            e.Handled = true;
            return;
        }

        flattenItem.Tag = columnIndex;
        flattenItem.IsEnabled = tableModel?.CanFlattenColumn(columnIndex) == true;
    }

    private void FlattenColumn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: int columnIndex } ||
            tableModel is null ||
            tableModel.TryCreateFlattenedColumn(columnIndex) is not { } flattenedTable)
            return;

        var selectedSourceIndex = tableModel.Rows.FirstOrDefault()?.SourceIndex ?? 0;
        var selectedColumnIndex = columnIndex;
        if (TryGetSelectedTableCell(out _, out _, out var currentColumnIndex, out var currentSourceIndex))
        {
            selectedSourceIndex = currentSourceIndex;
            selectedColumnIndex = currentColumnIndex;
        }

        var path = GetFlattenedColumnPath(columnIndex);
        tableHistory.Push(new(tableModel, tablePath, selectedSourceIndex, selectedColumnIndex));
        ShowTable(flattenedTable, path);
        Dispatcher.BeginInvoke(() => TableGrid.Focus(), DispatcherPriority.Input);
    }

    private void OriginalRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button
            {
                DataContext: SnapshotTableRow
                {
                    Origin: { } origin
                }
            } ||
            !tableHistory.TryPop(out var parent))
            return;

        ShowTable(parent.Model, parent.Path);
        RestoreTableSelection(origin.ParentSourceIndex, origin.ParentColumnIndex);
    }

    private void OriginalRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button
            {
                DataContext: SnapshotTableRow
                {
                    Origin: { } origin
                }
            } button)
            return;

        var path = GetOriginalRowPath(origin);
        button.ToolTip = string.IsNullOrEmpty(path)
            ? AppLocalization.Text(languageMode, "Inspector.OriginalRowTooltip")
            : $"{AppLocalization.Text(languageMode, "Inspector.OriginalRowTooltip")}{Environment.NewLine}{path}";
    }

    private void UpdateOpenCellTableState()
    {
        OpenCellTableButton.IsEnabled =
            TryGetSelectedTableCell(out var cell, out _, out _, out _) &&
            cell.Source is not null &&
            SnapshotTableModel.CanCreate(cell.Source);
    }

    private bool TryGetSelectedTableCell(
        out SnapshotTableCell cell,
        out string path,
        out int columnIndex,
        out int sourceIndex)
    {
        cell = SnapshotTableCell.Missing;
        path = string.Empty;
        columnIndex = -1;
        sourceIndex = -1;
        if (tableModel is null) return false;
        var selectedCell = TableGrid.CurrentCell.IsValid
            ? TableGrid.CurrentCell
            : TableGrid.SelectedCells.FirstOrDefault();
        if (!selectedCell.IsValid ||
            selectedCell.Item is not SnapshotTableRow row ||
            !tableColumnIndexes.TryGetValue(selectedCell.Column, out columnIndex) ||
            !tableModel.TryGetCell(row, columnIndex, out cell))
            return false;

        sourceIndex = row.SourceIndex;
        var rowPath = tableModel.SourceRowsAreItems
            ? $"{tablePath}[{row.SourceIndex}]"
            : tablePath;
        path = tableModel.HasSyntheticValueColumn
            ? rowPath
            : AppendPropertyPath(rowPath, tableModel.Columns[columnIndex]);
        return cell.Source is not null;
    }

    private string GetOriginalRowPath(SnapshotTableRowOrigin origin)
    {
        if (!tableHistory.TryPeek(out var parent) ||
            origin.ParentColumnIndex < 0 ||
            origin.ParentColumnIndex >= parent.Model.Columns.Count)
            return string.Empty;

        var rowPath = parent.Model.SourceRowsAreItems
            ? $"{parent.Path}[{origin.ParentSourceIndex}]"
            : parent.Path;
        var cellPath = parent.Model.HasSyntheticValueColumn
            ? rowPath
            : AppendPropertyPath(rowPath, parent.Model.Columns[origin.ParentColumnIndex]);
        return origin.ItemIndex is { } itemIndex ? $"{cellPath}[{itemIndex}]" : cellPath;
    }

    private string GetFlattenedColumnPath(int columnIndex)
    {
        if (tableModel is null || columnIndex < 0 || columnIndex >= tableModel.Columns.Count)
            return tablePath;

        var rowPath = tableModel.SourceRowsAreItems ? $"{tablePath}[*]" : tablePath;
        return tableModel.HasSyntheticValueColumn
            ? rowPath
            : AppendPropertyPath(rowPath, tableModel.Columns[columnIndex]);
    }

    private static string AppendPropertyPath(string path, string propertyName) =>
        IsSimpleIdentifier(propertyName)
            ? $"{path}.{propertyName}"
            : $"{path}[{SnapshotTextFormatter.QuoteJsonString(propertyName)}]";

    private static bool IsSimpleIdentifier(string value) =>
        value.Length > 0 &&
        (char.IsLetter(value[0]) || value[0] == '_') &&
        value.Skip(1).All(static character => char.IsLetterOrDigit(character) || character == '_');

    private void ConfigureTable()
    {
        isConfiguringTable = true;
        try
        {
            tableCacheWarmupTimer.Stop();
            TableGrid.ItemsSource = null;
            var rowCount = tableModel?.Rows.Count ?? 0;
            targetTableCachedRowCount = TableGridPerformance.CalculateCachedRowCount(rowCount);
            currentTableCachedRowCount = TableGridPerformance.CalculateCachedRowCount(
                rowCount,
                TableGridPerformance.InitialCachedRowCount);
            TableGridPerformance.Configure(
                TableGrid,
                rowCount,
                currentTableCachedRowCount);
            TableGrid.UnselectAllCells();
            TableGrid.CurrentCell = new();
            TableGrid.FrozenColumnCount = 0;
            TableGrid.Columns.Clear();
            tableColumnIndexes.Clear();
            tableSortStates.Clear();
            tableSortGlyphs.Clear();
            tableSortHeaders.Clear();
            OpenCellTableButton.IsEnabled = false;
            TableBackButton.IsEnabled = tableHistory.Count > 0;
            TableModeButton.IsEnabled = tableModel is not null;
            if (tableModel is null) return;

            if (tableModel.HasRowOrigins)
            {
                var header = new TextBlock();
                header.SetResourceReference(TextBlock.TextProperty, "Inspector.OriginalRow");
                TableGrid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = header,
                    CellTemplate = (DataTemplate)FindResource("OriginalRowCellTemplate"),
                    CanUserSort = false,
                    MinWidth = 88,
                    Width = DataGridLength.Auto
                });
                TableGrid.FrozenColumnCount = 1;
            }

            for (var index = 0; index < tableModel.Columns.Count; index++)
            {
                var columnIndex = index;
                var profile = tableModel.GetColumnProfile(columnIndex);
                var canFlatten = tableModel.CanFlattenColumn(columnIndex);
                var elementStyle = new Style(typeof(TextBlock));
                elementStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
                elementStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
                elementStyle.Setters.Add(new Setter(ToolTipService.ToolTipProperty,
                    new Binding($"Cells[{columnIndex}].Display")));
                var column = new DataGridTextColumn
                {
                    Binding = new Binding($"Cells[{columnIndex}].Display") { Mode = BindingMode.OneWay },
                    ClipboardContentBinding = new Binding($"Cells[{columnIndex}].ExportValue") { Mode = BindingMode.OneWay },
                    ElementStyle = elementStyle,
                    MinWidth = 80,
                    MaxWidth = 420,
                    Width = new DataGridLength(140),
                    SortMemberPath = $"Cells[{columnIndex}].Display"
                };
                column.Header = CreateTableSortHeader(
                    column,
                    tableModel.Columns[columnIndex],
                    profile,
                    canFlatten);
                TableGrid.Columns.Add(column);
                tableColumnIndexes.Add(column, columnIndex);
                tableSortStates.Add(column, TableSortState.Original);
                UpdateTableSortHeader(column);
            }
            // Attach the potentially large row source after all columns are ready. Adding
            // columns to a live ItemsSource repeatedly invalidates the DataGrid layout.
            TableGrid.ItemsSource = tableModel.Rows;
            UpdateTableSummary();
            StartTableCacheWarmup();
        }
        finally
        {
            isConfiguringTable = false;
        }
        UpdateOpenCellTableState();
    }

    private FrameworkElement CreateTableSortHeader(
        DataGridColumn column,
        string name,
        SnapshotTableColumnProfile profile,
        bool canFlatten)
    {
        var header = new TableSortHeader(
            name,
            profile.IsMixed ? $"{name} ⚠" : name);
        header.SortGlyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        header.ToolTip = BuildTableColumnHeaderTooltip(profile, canFlatten);
        tableSortGlyphs.Add(column, header.SortGlyph);
        tableSortHeaders.Add(column, header);
        return header;
    }

    private void UpdateTableSortHeader(DataGridColumn column)
    {
        if (!tableSortStates.TryGetValue(column, out var state) ||
            !tableSortGlyphs.TryGetValue(column, out var glyph) ||
            !tableSortHeaders.TryGetValue(column, out var header) ||
            !tableColumnIndexes.TryGetValue(column, out var columnIndex) ||
            tableModel is null)
            return;

        var profile = tableModel.GetColumnProfile(columnIndex);
        glyph.Text = TableSortCycle.Glyph(state);
        header.ToolTip = BuildTableColumnHeaderTooltip(
            profile,
            tableModel.CanFlattenColumn(columnIndex));
        var stateKey = state switch
        {
            TableSortState.Ascending => "Inspector.SortAscending",
            TableSortState.Descending => "Inspector.SortDescending",
            _ => "Inspector.SortOriginal"
        };
        AutomationProperties.SetName(
            header,
            $"{tableModel.Columns[columnIndex]}, {AppLocalization.Text(languageMode, stateKey)}");
    }

    private string BuildTableColumnHeaderTooltip(
        SnapshotTableColumnProfile profile,
        bool canFlatten)
    {
        var parts = new List<string>();
        if (profile.IsMixed)
            parts.Add(AppLocalization.Text(
                languageMode,
                "Inspector.MixedColumnTooltip",
                profile.SequenceCount,
                profile.ObjectCount,
                profile.ScalarCount,
                profile.NullCount));
        if (canFlatten)
            parts.Add(AppLocalization.Text(languageMode, "Inspector.FlattenColumnTooltip"));
        parts.Add(AppLocalization.Text(languageMode, "Inspector.SortColumnTooltip"));
        return string.Join(Environment.NewLine, parts);
    }

    private void RestoreTableSelection(int sourceIndex, int columnIndex)
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                if (tableModel is null) return;
                var row = tableModel.Rows.FirstOrDefault(candidate => candidate.SourceIndex == sourceIndex);
                var column = tableColumnIndexes
                    .FirstOrDefault(pair => pair.Value == columnIndex)
                    .Key;
                if (row is null || column is null)
                {
                    TableGrid.Focus();
                    return;
                }

                TableGrid.SelectedItem = row;
                TableGrid.CurrentCell = new(row, column);
                TableGrid.ScrollIntoView(row, column);
                TableGrid.Focus();
            },
            DispatcherPriority.Input);
    }

    private void SetTableMode(bool tableMode)
    {
        if (tableMode && tableModel is null) return;
        isTableMode = tableMode;
        var treeVisibility = tableMode ? Visibility.Collapsed : Visibility.Visible;
        TreeSearchPanel.Visibility = treeVisibility;
        SnapshotTree.Visibility = treeVisibility;
        TreeSplitter.Visibility = treeVisibility;
        TreeSelectionPanel.Visibility = treeVisibility;
        DetailText.Visibility = treeVisibility;
        TablePanel.Visibility = tableMode ? Visibility.Visible : Visibility.Collapsed;
        if (tableMode) StartTableCacheWarmup();
        else tableCacheWarmupTimer.Stop();
        ExpandSelectedButton.Visibility = treeVisibility;
        CollapseSelectedButton.Visibility = treeVisibility;
        CopySelectedButton.Visibility = treeVisibility;
        CopyPathButton.Visibility = treeVisibility;
        TableModeButton.IsEnabled = tableMode
            ? tableModel is not null
            : selectedNode is not null && SnapshotTableModel.CanCreate(selectedNode.Snapshot);
        UpdateViewModeButtons();
        RestoreStatusDisplays();
        Dispatcher.BeginInvoke(
            () =>
            {
                if (tableMode) TableGrid.Focus();
                else FocusFirstResult();
            },
            DispatcherPriority.Input);
    }

    private void StartTableCacheWarmup()
    {
        if (!IsLoaded || !isTableMode ||
            currentTableCachedRowCount >= targetTableCachedRowCount)
            return;
        tableCacheWarmupTimer.Start();
    }

    private void TableCacheWarmupTimer_Tick(object? sender, EventArgs e)
    {
        if (!isTableMode || TableGrid.ItemsSource is null)
        {
            tableCacheWarmupTimer.Stop();
            return;
        }
        if (TableGrid.IsMouseCaptureWithin) return;

        currentTableCachedRowCount = TableGridPerformance.NextCachedRowCount(
            currentTableCachedRowCount,
            targetTableCachedRowCount);
        TableGridPerformance.SetCachedRowCount(TableGrid, currentTableCachedRowCount);
        if (currentTableCachedRowCount >= targetTableCachedRowCount)
            tableCacheWarmupTimer.Stop();
    }

    private void UpdateViewModeButtons()
    {
        SetViewModeButtonState(TreeModeButton, selected: !isTableMode);
        SetViewModeButtonState(TableModeButton, selected: isTableMode);
    }

    private static void SetViewModeButtonState(Button button, bool selected)
    {
        button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        button.SetResourceReference(
            Control.BackgroundProperty,
            selected ? "SelectionBrush" : "PanelBrush");
        button.SetResourceReference(
            Control.BorderBrushProperty,
            selected ? "AccentBrush" : "BorderBrush");
    }

    private void UpdateTableSummary()
    {
        if (tableModel is null) return;
        var parts = new List<string>
        {
            tablePath,
            AppLocalization.Text(languageMode, "Inspector.TableSummary", tableModel.Rows.Count, tableModel.Columns.Count)
        };
        if (tableModel.TotalRowCount is { } total && total > tableModel.Rows.Count)
            parts.Add(AppLocalization.Text(
                languageMode, "Inspector.TableRowsLimited", total, tableModel.Rows.Count));
        else if (tableModel.RowsTruncated)
            parts.Add(AppLocalization.Text(
                languageMode, "Inspector.TableRowsCaptureLimited", tableModel.Rows.Count));
        if (tableModel.ColumnsTruncated)
            parts.Add(AppLocalization.Text(
                languageMode, "Inspector.TableColumnsLimited", tableModel.Columns.Count));
        if (tableModel.FlattenedColumnProfile is { } profile)
        {
            parts.Add(AppLocalization.Text(
                languageMode,
                "Inspector.FlattenedSummary",
                profile.SequenceCount,
                profile.ObjectCount));
            if (profile.ExcludedCount > 0)
                parts.Add(AppLocalization.Text(
                    languageMode,
                    "Inspector.FlattenedExcluded",
                    profile.ExcludedCount));
        }
        tableSummaryStatus = string.Join(" · ", parts);
        if (!notificationTimer.IsEnabled) TableStatus.Text = tableSummaryStatus;
    }

    private void SetSelectedNode(SnapshotTreeNode node)
    {
        selectedNode = node;
        UpdateSelectionActions(true);
        DetailText.Text = node.Detail;
        PathText.Text = node.Path;
        if (!isTableMode) TableModeButton.IsEnabled = SnapshotTableModel.CanCreate(node.Snapshot);
    }

    private void ClearSelection()
    {
        if (selectedNode is not null) selectedNode.IsSelected = false;
        selectedNode = null;
        UpdateSelectionActions(false);
        DetailText.Clear();
        PathText.Text = string.Empty;
        if (!isTableMode) TableModeButton.IsEnabled = false;
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
        if (isTableMode) TableStatus.Text = text;
    }

    private void ShowNotification(string key, params object?[] arguments)
    {
        notificationTimer.Stop();
        var text = AppLocalization.Text(languageMode, key, arguments);
        SearchStatus.Text = text;
        TableStatus.Text = text;
        notificationTimer.Start();
    }

    private void RestoreStatusDisplays()
    {
        SearchStatus.Text = currentSearchStatus;
        TableStatus.Text = tableSummaryStatus;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private sealed record TableNavigationState(
        SnapshotTableModel Model,
        string Path,
        int SelectedSourceIndex,
        int SelectedColumnIndex);
}
