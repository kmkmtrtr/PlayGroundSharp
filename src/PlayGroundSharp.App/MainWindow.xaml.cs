using System.ComponentModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.Win32;
using PlayGroundSharp.Core;
using PlayGroundSharp.LanguageService;

namespace PlayGroundSharp.App;

public partial class MainWindow : Window
{
    private const int WmMouseHorizontalWheel = 0x020E;
    private const int MaximumDroppedPathCount = 500;
    private const double DualPaneMinimumWidth = 1050;
    private readonly MainViewModel viewModel = new();
    private IReadOnlyList<CompletionCandidate> allCompletionItems = [];
    private CancellationTokenSource? completionCancellation;
    private CancellationTokenSource? completionDescriptionCancellation;
    private CancellationTokenSource? signatureHelpCancellation;
    private CancellationTokenSource? hoverQuickInfoCancellation;
    private CancellationTokenSource? pinnedQuickInfoCancellation;
    private readonly DispatcherTimer signatureHelpTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly DispatcherTimer completionDescriptionTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly DispatcherTimer quickInfoChordTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private AssistMode assistMode;
    private int completionFilterStart;
    private string? lastCompletionFilterPrefix;
    private GridLength typeExplorerWidth = new(286);
    private GridLength referenceDrawerWidth = new(470);
    private GridLength completionListWidth = new(390);
    private HwndSource? windowSource;
    private HelpWindow? helpWindow;
    private AppLanguageMode? helpWindowLanguage;
    private bool transcriptAutoScroll = true;
    private bool copyInProgress;
    private Action? quickInfoChordStatusRestore;
    private bool quickInfoChordPending;
    private WindowState lastNonMinimizedWindowState = WindowState.Normal;
    private bool typeExplorerAutoCollapsed;
    private bool closeInProgress;
    private bool closeCompleted;

    public MainWindow()
    {
        App.ApplyLanguage(viewModel.LanguageMode);
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateEditorAccessibilityName();
        ApplySavedWindowLayout();
        App.ApplyTheme(viewModel.ThemeMode);
        Editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");
        viewModel.Transcript.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset && viewModel.Transcript.Count == 0)
                SetTranscriptAutoScroll(true);
            Dispatcher.BeginInvoke(() =>
            {
                if (transcriptAutoScroll) TranscriptScroll.ScrollToEnd();
            });
        };
        Editor.TextArea.TextEntered += async (_, args) =>
        {
            if (args.Text == ":") await ShowCompletionAsync();
            if (args.Text == ".") await ShowCompletionAsync();
            if (args.Text == "(" || args.Text == "," && assistMode == AssistMode.Signature)
                await ShowSignatureHelpAsync();
            if (args.Text == ")" && assistMode == AssistMode.Signature) ScheduleSignatureHelpRefresh();
        };
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            viewModel.UpdateCursorPosition(Editor.TextArea.Caret.Line, Editor.TextArea.Caret.Column);
            ClosePinnedQuickInfo();
            if (assistMode == AssistMode.Signature) ScheduleSignatureHelpRefresh();
            else if (assistMode == AssistMode.Completion) ApplyCompletionFilter();
        };
        signatureHelpTimer.Tick += async (_, _) =>
        {
            signatureHelpTimer.Stop();
            await ShowSignatureHelpAsync();
        };
        completionDescriptionTimer.Tick += async (_, _) =>
        {
            completionDescriptionTimer.Stop();
            await LoadCompletionDescriptionAsync();
        };
        quickInfoChordTimer.Tick += (_, _) => CancelQuickInfoChord();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.InputText))
        {
            if (!closeInProgress && !string.Equals(Editor.Text, viewModel.InputText, StringComparison.Ordinal))
            {
                Editor.Text = viewModel.InputText;
                Editor.CaretOffset = Editor.Text.Length;
            }
            return;
        }
        if (e.PropertyName != nameof(MainViewModel.LanguageMode)) return;
        UpdateEditorAccessibilityName();
        if (helpWindow is { IsVisible: true } help)
        {
            help.ApplyLanguage(viewModel.LanguageMode);
            helpWindowLanguage = viewModel.LanguageMode;
        }
    }

    private void UpdateEditorAccessibilityName() =>
        AutomationProperties.SetName(Editor.TextArea, viewModel.Localize("Input.Editor"));

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureResponsivePanes();
        windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        windowSource?.AddHook(WindowMessageHook);
        Editor.Focus();
        await viewModel.InitializeAsync();
        if (!closeInProgress) Editor.Focus();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closeCompleted) return;
        e.Cancel = true;
        if (closeInProgress) return;
        closeInProgress = true;
        IsEnabled = false;
        SaveWindowLayout();
        windowSource?.RemoveHook(WindowMessageHook);
        windowSource = null;
        CancelAndDispose(ref completionCancellation);
        CancelAndDispose(ref completionDescriptionCancellation);
        CancelAndDispose(ref signatureHelpCancellation);
        signatureHelpTimer.Stop();
        completionDescriptionTimer.Stop();
        quickInfoChordTimer.Stop();
        quickInfoChordStatusRestore = null;
        CancelAndDispose(ref hoverQuickInfoCancellation);
        CancelAndDispose(ref pinnedQuickInfoCancellation);
        try
        {
            await viewModel.DisposeAsync();
        }
        catch (Exception error)
        {
            Debug.WriteLine($"Application shutdown cleanup failed: {error}");
        }
        finally
        {
            viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            closeCompleted = true;
            // DisposeAsync can complete synchronously when the window is closed before
            // Worker startup finishes. Calling Close again from inside the original
            // Closing event then trips WPF's "window is already closing" guard. Queue
            // the final close so the cancelled first close can unwind in every case.
            _ = Dispatcher.BeginInvoke(() => Close(), DispatcherPriority.Normal);
        }
    }

    private void ApplySavedWindowLayout()
    {
        var settings = viewModel.SavedSettings;
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;
        typeExplorerWidth = new(settings.TypeExplorerWidth);
        referenceDrawerWidth = new(settings.ReferenceDrawerWidth);
        completionListWidth = new(settings.CompletionListWidth);
        CompletionListColumn.Width = completionListWidth;
        WorkspaceTabs.SelectedIndex = settings.WorkspaceTabIndex;

        if (settings.WindowLeft is { } left && settings.WindowTop is { } top &&
            IsWindowPositionVisible(left, top, settings.WindowWidth, settings.WindowHeight))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        if (settings.IsWindowMaximized)
        {
            lastNonMinimizedWindowState = WindowState.Maximized;
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveWindowLayout()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        var explorerWidth = TypeExplorerColumn.ActualWidth > 0
            ? TypeExplorerColumn.ActualWidth
            : typeExplorerWidth.Value;
        var drawerWidth = ReferenceDrawerColumn.ActualWidth > 0
            ? ReferenceDrawerColumn.ActualWidth
            : referenceDrawerWidth.Value;
        viewModel.SaveWindowLayout(
            bounds,
            WindowState == WindowState.Maximized ||
            WindowState == WindowState.Minimized && lastNonMinimizedWindowState == WindowState.Maximized,
            viewModel.IsTypeExplorerOpen || typeExplorerAutoCollapsed,
            viewModel.IsReferenceDrawerOpen,
            explorerWidth,
            drawerWidth,
            WorkspaceTabs.SelectedIndex,
            completionListWidth.Value);
    }

    private static bool IsWindowPositionVisible(double left, double top, double width, double height)
    {
        var savedBounds = new Rect(left, top, width, height);
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        var intersection = Rect.Intersect(savedBounds, virtualScreen);
        return intersection.Width >= 120 && intersection.Height >= 80;
    }

    private void TypeExplorerTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        viewModel.SelectedExplorerNode = e.NewValue as SymbolExplorerNode;
        if (e.NewValue is not SymbolExplorerNode { Signature.Length: > 0 }) SymbolDetailPopup.IsOpen = false;
    }

    private void TypeExplorerTree_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext is not
            SymbolExplorerNode { Signature.Length: > 0 } node) return;
        viewModel.SelectedExplorerNode = node;
        Dispatcher.BeginInvoke(() =>
        {
            if (Equals(viewModel.SelectedExplorerNode, node)) SymbolDetailPopup.IsOpen = true;
        });
    }

    private void TypeExplorerTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            SymbolDetailPopup.IsOpen = false;
            TypeExplorerSearchBox.Focus();
            TypeExplorerSearchBox.SelectAll();
            return;
        }
        if (e.Key is not (Key.Enter or Key.Space) ||
            TypeExplorerTree.SelectedItem is not SymbolExplorerNode { Signature.Length: > 0 }) return;
        e.Handled = true;
        SymbolDetailPopup.IsOpen = true;
    }

    private void TypeExplorerTree_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        var scrollViewer = FindAncestor<ScrollViewer>(e.OriginalSource as DependencyObject) ??
                           FindDescendant<ScrollViewer>(TypeExplorerTree);
        if (scrollViewer is not { ScrollableWidth: > 0 }) return;
        ScrollHorizontally(scrollViewer, -e.Delta);
        e.Handled = true;
    }

    private void TypeExplorerPane_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            typeExplorerAutoCollapsed = false;
            if (IsLoaded && ActualWidth < DualPaneMinimumWidth && viewModel.IsReferenceDrawerOpen)
                viewModel.IsReferenceDrawerOpen = false;
            TypeExplorerColumn.Width = typeExplorerWidth;
            TypeExplorerSplitterColumn.Width = new(5);
            return;
        }
        if (TypeExplorerColumn.ActualWidth > 0) typeExplorerWidth = new(TypeExplorerColumn.ActualWidth);
        TypeExplorerColumn.Width = new(0);
        TypeExplorerSplitterColumn.Width = new(0);
        SymbolDetailPopup.IsOpen = false;
    }

    private void ReferenceDrawerPane_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            if (IsLoaded && ActualWidth < DualPaneMinimumWidth && viewModel.IsTypeExplorerOpen)
                viewModel.IsTypeExplorerOpen = false;
            ReferenceDrawerSplitterColumn.Width = new(5);
            ReferenceDrawerColumn.Width = referenceDrawerWidth;
            return;
        }
        if (ReferenceDrawerColumn.ActualWidth > 0) referenceDrawerWidth = new(ReferenceDrawerColumn.ActualWidth);
        ReferenceDrawerSplitterColumn.Width = new(0);
        ReferenceDrawerColumn.Width = new(0);
        if (typeExplorerAutoCollapsed && !viewModel.IsTypeExplorerOpen)
        {
            typeExplorerAutoCollapsed = false;
            viewModel.IsTypeExplorerOpen = true;
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded) EnsureResponsivePanes();
        if (IsLoaded && AssistPopup.IsOpen)
        {
            PrepareAssistPopupLayout();
            RefreshPopupPosition(AssistPopup);
        }
    }

    private void Window_LocationChanged(object? sender, EventArgs e)
    {
        RefreshPopupPosition(AssistPopup);
        RefreshPopupPosition(QuickInfoPopup);
        RefreshPopupPosition(SymbolDetailPopup);
    }

    private static void RefreshPopupPosition(Popup popup)
    {
        if (!popup.IsOpen) return;
        // Popup is a separate native window and does not always follow its placement
        // target. Invalidating the offset repositions it without losing selection.
        var offset = popup.HorizontalOffset;
        popup.HorizontalOffset = offset + 0.01;
        popup.HorizontalOffset = offset;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) lastNonMinimizedWindowState = WindowState;
    }

    private void EnsureResponsivePanes()
    {
        if (ActualWidth < DualPaneMinimumWidth)
        {
            if (!viewModel.IsTypeExplorerOpen || !viewModel.IsReferenceDrawerOpen) return;
            typeExplorerAutoCollapsed = true;
            viewModel.IsTypeExplorerOpen = false;
            return;
        }

        if (!typeExplorerAutoCollapsed || viewModel.IsTypeExplorerOpen) return;
        typeExplorerAutoCollapsed = false;
        viewModel.IsTypeExplorerOpen = true;
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (AssistPopup.IsOpen &&
            FindAncestor<ICSharpCode.AvalonEdit.TextEditor>(source) != Editor &&
            !IsDescendantOf(source, AssistPopupBorder))
            HideAssist();
        if (SymbolDetailPopup.IsOpen &&
            FindAncestor<TreeView>(source) != TypeExplorerTree)
            SymbolDetailPopup.IsOpen = false;
        if (QuickInfoPopup.IsOpen &&
            FindAncestor<ICSharpCode.AvalonEdit.TextEditor>(source) != Editor)
            ClosePinnedQuickInfo();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        SymbolDetailPopup.IsOpen = false;
        ClosePinnedQuickInfo();
        if (AssistPopup.IsOpen) HideAssist();
    }

    private IntPtr WindowMessageHook(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        if (message != WmMouseHorizontalWheel) return IntPtr.Zero;

        var delta = unchecked((short)((wordParameter.ToInt64() >> 16) & 0xffff));
        var screenX = unchecked((short)(longParameter.ToInt64() & 0xffff));
        var screenY = unchecked((short)((longParameter.ToInt64() >> 16) & 0xffff));
        var hit = InputHitTest(PointFromScreen(new Point(screenX, screenY))) as DependencyObject;
        var scrollViewer = FindAncestor<ScrollViewer>(hit);
        if (scrollViewer is not { ScrollableWidth: > 0 }) return IntPtr.Zero;

        ScrollHorizontally(scrollViewer, delta);
        handled = true;
        return IntPtr.Zero;
    }

    private static void ScrollHorizontally(ScrollViewer scrollViewer, int delta)
    {
        var distance = Math.Clamp(scrollViewer.ViewportWidth * 0.15, 36, 120);
        var offset = Math.Clamp(
            scrollViewer.HorizontalOffset + Math.Sign(delta) * distance,
            0,
            scrollViewer.ScrollableWidth);
        scrollViewer.ScrollToHorizontalOffset(offset);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = GetParent(current);
        }
        return null;
    }

    private static bool IsDescendantOf(DependencyObject? current, DependencyObject ancestor)
    {
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = GetParent(current);
        }
        return false;
    }

    private static DependencyObject? GetParent(DependencyObject current)
    {
        if (current is Visual or System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(current);
        if (current is FrameworkContentElement frameworkContent)
            return frameworkContent.Parent ?? ContentOperations.GetParent(frameworkContent);
        if (current is ContentElement content)
            return ContentOperations.GetParent(content);
        return LogicalTreeHelper.GetParent(current);
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } descendant) return descendant;
        }
        return null;
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F6 && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            e.Handled = true;
            MovePaneFocus(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1);
        }
        else if (e.Key == Key.F8 && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            e.Handled = true;
            NavigateToDiagnostic(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1);
        }
        else if (e.Key == Key.F1)
        {
            e.Handled = true;
            OpenHelp();
        }
        else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            await OpenWorkspaceAsync();
        }
        else if (e.Key == Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            await SaveWorkspaceAsync();
        }
        else if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            FocusEditor();
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            FocusExplorerSearch();
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            await CopyTranscriptAsync();
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None &&
                 FindAncestor<TreeView>(Keyboard.FocusedElement as DependencyObject) is
                 { DataContext: TranscriptLine { Snapshot: { } snapshot } })
        {
            e.Handled = true;
            OpenResultInspector(snapshot);
        }
        else if (e.Key == Key.Escape && SymbolDetailPopup.IsOpen)
        {
            e.Handled = true;
            SymbolDetailPopup.IsOpen = false;
            TypeExplorerTree.Focus();
        }
        else if (e.Key == Key.Escape && QuickInfoPopup.IsOpen)
        {
            e.Handled = true;
            ClosePinnedQuickInfo();
            Editor.Focus();
        }
        else if (e.Key == Key.Escape && AssistPopup.IsOpen)
        {
            e.Handled = true;
            HideAssist();
            Editor.Focus();
        }
        else if (e.Key == Key.Escape && viewModel.CanCancel)
        {
            e.Handled = true;
            await CancelFromUiAsync();
        }
    }

    private void MovePaneFocus(int delta)
    {
        var panes = new List<FrameworkElement>();
        if (viewModel.IsTypeExplorerOpen) panes.Add(TypeExplorerSearchBox);
        panes.Add(Editor);
        if (viewModel.IsReferenceDrawerOpen) panes.Add(WorkspaceTabs);
        var current = panes.FindIndex(static pane => pane.IsKeyboardFocusWithin);
        var next = current < 0
            ? delta < 0 ? panes.Count - 1 : 0
            : (current + Math.Sign(delta) + panes.Count) % panes.Count;
        panes[next].Focus();
        if (ReferenceEquals(panes[next], TypeExplorerSearchBox)) TypeExplorerSearchBox.SelectAll();
    }

    private void DiagnosticStatus_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        NavigateToDiagnostic(1);
    }

    private void NavigateToDiagnostic(int delta)
    {
        if (viewModel.MoveDiagnostic(delta) is not { } diagnostic) return;
        HideAssist();
        var startLine = Math.Clamp(diagnostic.StartLine, 1, Editor.Document.LineCount);
        var endLine = Math.Clamp(diagnostic.EndLine, startLine, Editor.Document.LineCount);
        var startDocumentLine = Editor.Document.GetLineByNumber(startLine);
        var endDocumentLine = Editor.Document.GetLineByNumber(endLine);
        var start = startDocumentLine.Offset + Math.Clamp(diagnostic.StartColumn - 1, 0, startDocumentLine.Length);
        var end = endDocumentLine.Offset + Math.Clamp(diagnostic.EndColumn - 1, 0, endDocumentLine.Length);
        Editor.Select(start, Math.Max(0, end - start));
        Editor.CaretOffset = start;
        Editor.ScrollTo(startLine, Math.Max(1, diagnostic.StartColumn));
        Editor.Focus();
    }

    private async void SaveWorkspace_Click(object sender, RoutedEventArgs e) => await SaveWorkspaceAsync();

    private async Task SaveWorkspaceAsync()
    {
        if (!viewModel.CanSaveWorkspace)
        {
            viewModel.SetLocalizedStatus("Status.SessionBusy");
            FocusEditor();
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = viewModel.Localize("Menu.SaveWorkspace"),
            Filter = viewModel.Localize("Dialog.WorkspaceFilter"),
            DefaultExt = ".pgsworkspace",
            AddExtension = true,
            FileName = $"PlayGroundSharp-{DateTime.Now:yyyyMMdd-HHmm}.pgsworkspace"
        };
        if (dialog.ShowDialog(this) != true)
        {
            FocusEditor();
            return;
        }
        try
        {
            viewModel.SetLocalizedStatus("Status.SavingWorkspace");
            await WorkspaceFile.SaveAsync(dialog.FileName, viewModel.CreateWorkspaceDocument());
            viewModel.Transcript.Add(TranscriptLine.System(viewModel.Localize("Message.WorkspaceSaved", dialog.FileName)));
            viewModel.SetLocalizedStatus("Status.Ready");
        }
        catch (Exception error)
        {
            viewModel.SetLocalizedStatus("Status.Ready");
            ShowError(error);
        }
        finally
        {
            FocusEditor();
        }
    }

    private async void OpenWorkspace_Click(object sender, RoutedEventArgs e) => await OpenWorkspaceAsync();

    private async Task OpenWorkspaceAsync()
    {
        if (!viewModel.CanOpenWorkspace)
        {
            viewModel.SetLocalizedStatus("Status.SessionBusy");
            FocusEditor();
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = viewModel.Localize("Dialog.WorkspaceLoadTitle"),
            Filter = viewModel.Localize("Dialog.WorkspaceFilter"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            FocusEditor();
            return;
        }
        await OpenWorkspaceFileAsync(dialog.FileName);
    }

    private async Task OpenWorkspaceFileAsync(string path)
    {
        if (!viewModel.CanOpenWorkspace)
        {
            viewModel.SetLocalizedStatus("Status.SessionBusy");
            FocusEditor();
            return;
        }
        if (MessageBox.Show(this, viewModel.Localize("Dialog.WorkspaceLoadWarning"),
                viewModel.Localize("Dialog.WorkspaceLoadTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) !=
            MessageBoxResult.OK)
        {
            FocusEditor();
            return;
        }
        try
        {
            var document = await WorkspaceFile.LoadAsync(path);
            await viewModel.LoadWorkspaceAsync(document);
            Editor.Text = viewModel.InputText;
            Editor.CaretOffset = Editor.Text.Length;
        }
        catch (Exception error)
        {
            ShowError(error);
        }
        finally
        {
            FocusEditor();
        }
    }

    private void InsertDataSnippet_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string operation }) return;
        var dialog = new OpenFileDialog
        {
            Title = viewModel.Localize("Menu.Data"),
            Filter = viewModel.Localize("Dialog.DataFileFilter"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            FocusEditor();
            return;
        }
        var literal = DataSnippetBuilder.ToVerbatimStringLiteral(dialog.FileName);
        var snippet = operation switch
        {
            "Inspect" => $"Data.Inspect({literal})",
            "Preview" => $"Data.PreviewText({literal}, 65536)",
            "Lines" => $"Data.ReadLines({literal}).Take(100)",
            "Json" => $"await Data.ReadJsonAsync({literal}, ExecutionCancellation)",
            "JsonArray" => $"await Data.ReadJsonArrayAsync({literal}, take: 100, cancellationToken: ExecutionCancellation)",
            "JsonLines" => DataSnippetBuilder.CreateJsonLines(dialog.FileName),
            _ => null
        };
        if (snippet is null) return;
        InsertDroppedSnippet(snippet);
        viewModel.ShowStatusNotification("Status.SnippetInserted");
    }

    private void Editor_PreviewDragOver(object sender, DragEventArgs e)
    {
        var canDrop = e.Data.GetDataPresent(DataFormats.FileDrop) &&
                      e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 };
        e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = canDrop ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void Editor_PreviewDragLeave(object sender, DragEventArgs e) =>
        DropOverlay.Visibility = Visibility.Collapsed;

    private void Editor_PreviewDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!TryGetDroppedPaths(e.Data, out var paths, out var omittedCount))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (Editor.GetPositionFromPoint(e.GetPosition(Editor)) is { } position)
            Editor.CaretOffset = Editor.Document.GetOffset(position.Location);
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        HideAssist();
        Editor.Focus();
        if (omittedCount > 0)
            viewModel.Transcript.Add(TranscriptLine.Diagnostic(
                viewModel.Localize("Message.DropPathsLimited", MaximumDroppedPathCount, omittedCount),
                error: false));

        if (paths.Length > 1)
        {
            ShowMultipleDropActionMenu(paths);
            return;
        }
        ShowDropActionMenu(paths[0]);
    }

    private void ShowMultipleDropActionMenu(IReadOnlyList<string> paths)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = Editor,
            Placement = PlacementMode.MousePoint
        };
        menu.Items.Add(CreateDropAction("Drop.InsertPathArray", DataSnippetBuilder.CreatePathArray(paths)));
        if (paths.All(File.Exists))
        {
            menu.Items.Add(CreateDropAction("Drop.InspectFiles", DataSnippetBuilder.CreateFileInspection(paths)));
            if (paths.All(static path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)))
                menu.Items.Add(CreateDropAction("Drop.ReadJsonFiles", DataSnippetBuilder.CreateJsonBatch(paths)));
        }
        OpenDropActionMenu(menu);
    }

    private void ShowDropActionMenu(string path)
    {
        var literal = DataSnippetBuilder.ToVerbatimStringLiteral(path);
        var menu = new ContextMenu
        {
            PlacementTarget = Editor,
            Placement = PlacementMode.MousePoint
        };
        menu.Items.Add(CreateDropAction("Drop.InsertPath", literal));
        menu.Items.Add(new Separator());

        if (Directory.Exists(path))
        {
            menu.Items.Add(CreateDropAction(
                "Drop.EnumerateFiles",
                $"System.IO.Directory.EnumerateFiles({literal})"));
            menu.Items.Add(CreateDropAction(
                "Drop.EnumerateFilesRecursive",
                $"System.IO.Directory.EnumerateFiles({literal}, \"*\", System.IO.SearchOption.AllDirectories)"));
        }
        else
        {
            var extension = Path.GetExtension(path);
            if (extension.Equals(".pgsworkspace", StringComparison.OrdinalIgnoreCase))
            {
                menu.Items.Add(CreateDropWorkspaceAction(path));
                menu.Items.Add(new Separator());
            }
            menu.Items.Add(CreateDropAction("Drop.FileInfo", $"Data.Inspect({literal})"));
            if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                menu.Items.Add(CreateDropReferenceAction(path));
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                menu.Items.Add(CreateDropAction("Drop.ReadJson", $"await Data.ReadJsonAsync({literal}, ExecutionCancellation)"));
                menu.Items.Add(CreateDropAction("Drop.ReadJsonArray", $"await Data.ReadJsonArrayAsync({literal}, take: 100, cancellationToken: ExecutionCancellation)"));
            }
            else if (extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".ndjson", StringComparison.OrdinalIgnoreCase))
            {
                menu.Items.Add(CreateDropAction("Drop.ReadJsonLines", DataSnippetBuilder.CreateJsonLines(path)));
            }

            if (IsTextFile(extension))
            {
                menu.Items.Add(CreateDropAction("Drop.TextPreview", $"Data.PreviewText({literal}, 65536)"));
                menu.Items.Add(CreateDropAction("Drop.ReadLines", $"Data.ReadLines({literal}).Take(100)"));
            }
            else
            {
                menu.Items.Add(CreateDropAction("Drop.ReadBytes", $"Data.ReadBytes({literal}, count: 65536)"));
            }
        }

        OpenDropActionMenu(menu);
    }

    private void OpenDropActionMenu(ContextMenu menu)
    {
        var restoreStatus = viewModel.CaptureStatusRestore();
        foreach (var item in menu.Items.OfType<MenuItem>())
            item.Click += (_, _) => menu.Tag = true;
        menu.Closed += (_, _) =>
        {
            if (menu.Tag is not true) restoreStatus();
            FocusEditor();
        };
        menu.IsOpen = true;
        viewModel.SetStatusOverlay("Status.DropChooseAction");
    }

    private MenuItem CreateDropAction(string localizationKey, string snippet)
    {
        var item = new MenuItem { Header = viewModel.Localize(localizationKey) };
        item.Click += (_, _) =>
        {
            InsertDroppedSnippet(snippet);
            viewModel.ShowStatusNotification("Status.DroppedPath");
        };
        return item;
    }

    private MenuItem CreateDropReferenceAction(string path)
    {
        var item = new MenuItem { Header = viewModel.Localize("Drop.AddReference") };
        item.Click += async (_, _) => await AddReferencesFromUiAsync([path]);
        return item;
    }

    private MenuItem CreateDropWorkspaceAction(string path)
    {
        var item = new MenuItem { Header = viewModel.Localize("Drop.OpenWorkspace") };
        item.Click += async (_, _) => await OpenWorkspaceFileAsync(path);
        return item;
    }

    private void InsertDroppedSnippet(string snippet)
    {
        var start = Editor.SelectionStart;
        Editor.Document.Replace(start, Editor.SelectionLength, snippet);
        Editor.CaretOffset = start + snippet.Length;
        Editor.Focus();
    }

    private static bool TryGetDroppedPaths(IDataObject data, out string[] paths, out int omittedCount)
    {
        var availablePaths = data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] dropped
            ? dropped.Where(static path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        omittedCount = Math.Max(0, availablePaths.Length - MaximumDroppedPathCount);
        paths = availablePaths.Take(MaximumDroppedPathCount).ToArray();
        return paths.Length > 0;
    }

    private static bool IsTextFile(string extension) => extension.ToLowerInvariant() is
        ".txt" or ".log" or ".csv" or ".tsv" or ".md" or ".xml" or ".yaml" or ".yml" or
        ".json" or ".jsonl" or ".ndjson" or ".cs" or ".csx";

    private void OpenSymbolDocumentation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SymbolExplorerNode { DocumentationPath: { } path } }) return;
        var locale = viewModel.LanguageMode == AppLanguageMode.Japanese ? "ja-jp" : "en-us";
        try
        {
            Process.Start(new ProcessStartInfo($"https://learn.microsoft.com/{locale}/dotnet/api/{path}?view=net-10.0")
            {
                UseShellExecute = true
            });
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private void OpenHelp_Click(object sender, RoutedEventArgs e) => OpenHelp();

    private void OpenHelp()
    {
        if (helpWindow is { IsVisible: true } existing && helpWindowLanguage != viewModel.LanguageMode)
            existing.Close();
        if (helpWindow is { IsVisible: true })
        {
            if (helpWindow.WindowState == WindowState.Minimized)
                helpWindow.WindowState = WindowState.Normal;
            helpWindow.Activate();
            return;
        }

        var window = new HelpWindow(viewModel.LanguageMode) { Owner = this };
        helpWindow = window;
        helpWindowLanguage = viewModel.LanguageMode;
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(helpWindow, window)) return;
            helpWindow = null;
            helpWindowLanguage = null;
        };
        window.Show();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, viewModel.Localize("About.Message"), "PlayGroundSharp", MessageBoxButton.OK, MessageBoxImage.Information);
        FocusEditor();
    }

    private async void AddDllReference_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.CanChangeSession) return;
        var dialog = new OpenFileDialog
        {
            Title = viewModel.Localize("Dialog.AssemblyLoadTitle"),
            Filter = viewModel.Localize("Dialog.AssemblyFileFilter"),
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        await AddReferencesFromUiAsync(dialog.FileNames);
    }

    private async Task AddReferencesFromUiAsync(IReadOnlyList<string> paths)
    {
        var added = 0;
        try
        {
            viewModel.SetStatusOverlay("Status.AddingReferences");
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                if (await viewModel.AddReferenceAsync(path)) added++;
            if (added > 0) viewModel.ShowStatusNotification("Status.ReferencesAdded", added);
            else viewModel.SetLocalizedStatus("Status.Ready");
        }
        catch (Exception error)
        {
            viewModel.SetLocalizedStatus("Status.Ready");
            ShowError(error);
        }
        finally
        {
            FocusEditor();
        }
    }

    private void FocusInput_Click(object sender, RoutedEventArgs e) => FocusEditor();

    private void FocusExplorerSearch_Click(object sender, RoutedEventArgs e) => FocusExplorerSearch();

    private void FocusExplorerSearch()
    {
        viewModel.IsTypeExplorerOpen = true;
        Dispatcher.BeginInvoke(() =>
        {
            TypeExplorerSearchBox.Focus();
            TypeExplorerSearchBox.SelectAll();
        });
    }

    private void FocusEditorAfterCommand_Click(object sender, RoutedEventArgs e) => FocusEditor();

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        await CancelFromUiAsync();
        FocusEditor();
    }

    private async void RestartWorker_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.HasSessionState && MessageBox.Show(
                this,
                viewModel.Localize("Dialog.RestartWarning"),
                viewModel.Localize("Dialog.RestartTitle"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            FocusEditor();
            return;
        }
        try
        {
            if (viewModel.RestartWorkerCommand.CanExecute(null))
                await viewModel.RestartWorkerCommand.ExecuteAsync(null);
        }
        catch (Exception error)
        {
            ShowError(error);
        }
        FocusEditor();
    }

    private async void ResetSession_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.HasSessionState && MessageBox.Show(
                this,
                viewModel.Localize("Dialog.ResetWarning"),
                viewModel.Localize("Dialog.ResetTitle"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            FocusEditor();
            return;
        }
        try
        {
            if (viewModel.ResetCommand.CanExecute(null)) await viewModel.ResetCommand.ExecuteAsync(null);
        }
        catch (Exception error)
        {
            ShowError(error);
        }
        FocusEditor();
    }

    private async void RemoveUsing_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: string @namespace }) return;
        await RemoveUsingWithConfirmationAsync(@namespace);
    }

    private async void UsingList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            NewUsingBox.Focus();
            return;
        }
        if (e.Key != Key.Delete || !viewModel.CanChangeSession ||
            UsingList.SelectedItem is not string @namespace) return;
        e.Handled = true;
        await RemoveUsingWithConfirmationAsync(@namespace);
    }

    private async Task RemoveUsingWithConfirmationAsync(string @namespace)
    {
        if (viewModel.HasSessionState && MessageBox.Show(this,
                viewModel.Localize("Dialog.UsingRemoveWarning", @namespace),
                viewModel.Localize("Dialog.UsingRemoveTitle"),
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            await viewModel.RemoveUsingAsync(@namespace);
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private void FocusEditor() => Dispatcher.BeginInvoke(Editor.Focus);

    private void VariableItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject)?.DataContext is not VariableItem item)
            return;
        e.Handled = true;
        InsertDroppedSnippet(item.Name);
    }

    private async void VariableItem_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            VariableSearchBox.Focus();
            VariableSearchBox.SelectAll();
            return;
        }
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            await CopySelectedVariableAsync();
            return;
        }
        if (e.Key is not (Key.Enter or Key.Space) || sender is not ListView { SelectedItem: VariableItem item })
            return;
        e.Handled = true;
        InsertDroppedSnippet(item.Name);
    }

    private void VariableItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is not { } row) return;
        row.IsSelected = true;
        row.Focus();
    }

    private void VariableList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (VariableList.SelectedItem is null) e.Handled = true;
    }

    private async void CopyVariable_Click(object sender, RoutedEventArgs e)
        => await CopySelectedVariableAsync();

    private async Task CopySelectedVariableAsync()
    {
        if (VariableList.SelectedItem is not VariableItem item) return;
        await CopyTextAsync(() => item.CopyText);
    }

    private async void PackageSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (textBox.Text.Length > 0) textBox.Clear();
            else FocusEditor();
            return;
        }
        if (e.Key == Key.Down && PackageSearchResults.Items.Count > 0)
        {
            e.Handled = true;
            PackageSearchResults.SelectedIndex = 0;
            PackageSearchResults.ScrollIntoView(PackageSearchResults.SelectedItem);
            PackageSearchResults.UpdateLayout();
            (PackageSearchResults.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (!viewModel.CanChangeSession) return;
        if (viewModel.SearchPackagesCommand.CanExecute(null))
            await viewModel.SearchPackagesCommand.ExecuteAsync(null);
    }

    private async void PackageSearchResults_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (FindAncestor<ComboBox>(e.OriginalSource as DependencyObject) is not null) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            PackageSearchBox.Focus();
            PackageSearchBox.SelectAll();
            return;
        }
        if (e.Key != Key.Enter || !viewModel.CanChangeSession ||
            PackageSearchResults.SelectedItem is not PackageSearchItem package) return;
        e.Handled = true;
        if (viewModel.InstallPackageCommand.CanExecute(package))
            await viewModel.InstallPackageCommand.ExecuteAsync(package);
    }

    private async void TypeExplorerSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (textBox.Text.Length > 0) textBox.Clear();
            else FocusEditor();
        }
        else if (e.Key is Key.Down or Key.Enter)
        {
            e.Handled = true;
            if (await viewModel.ApplyTypeExplorerFilterNowAsync() && TypeExplorerTree.Items.Count > 0)
                FocusFirstExplorerResult(descendToMatch: textBox.Text.Length > 0);
        }
    }

    private void FocusFirstExplorerResult(bool descendToMatch)
    {
        TypeExplorerTree.UpdateLayout();
        if (TypeExplorerTree.ItemContainerGenerator.ContainerFromIndex(0) is not TreeViewItem item) return;
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

    private void VariableSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (textBox.Text.Length > 0) textBox.Clear();
            else FocusEditor();
        }
        else if (e.Key is Key.Down or Key.Enter &&
                 ApplyVariableFilterAndGetFirst() is { } item)
        {
            e.Handled = true;
            VariableList.SelectedItem = item;
            VariableList.ScrollIntoView(item);
            VariableList.Focus();
        }
    }

    private VariableItem? ApplyVariableFilterAndGetFirst()
    {
        viewModel.ApplyVariableFilterNow();
        return viewModel.VariableItemsView.Cast<object>().FirstOrDefault() as VariableItem;
    }

    private async void NewUsingBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (textBox.Text.Length > 0) textBox.Clear();
            else FocusEditor();
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (!viewModel.CanChangeSession) return;
        if (viewModel.AddUsingFromGuiCommand.CanExecute(null))
            await viewModel.AddUsingFromGuiCommand.ExecuteAsync(null);
    }

    private void InspectVariable_Click(object sender, RoutedEventArgs e)
    {
        if (VariableList.SelectedItem is VariableItem item)
            new ResultInspectorWindow(item.Snapshot, viewModel) { Owner = this }.Show();
    }

    private async void CopyTranscript_Click(object sender, RoutedEventArgs e) => await CopyTranscriptAsync();

    private async Task CopyTranscriptAsync()
    {
        var lines = viewModel.Transcript.ToArray();
        await CopyTextAsync(() => FormatTranscript(lines));
    }

    private async void SaveTranscript_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = viewModel.Localize("Dialog.TranscriptSaveTitle"),
            Filter = viewModel.Localize("Dialog.TextFileFilter"),
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = $"PlayGroundSharp-transcript-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };
        if (dialog.ShowDialog(this) != true)
        {
            FocusEditor();
            return;
        }
        try
        {
            viewModel.SetStatusOverlay("Status.SavingResult");
            var lines = viewModel.Transcript.ToArray();
            await File.WriteAllTextAsync(dialog.FileName, await Task.Run(() => FormatTranscript(lines)));
            viewModel.ShowStatusNotification("Status.Saved", Path.GetFileName(dialog.FileName));
        }
        catch (Exception error)
        {
            viewModel.ShowStatusNotification("Status.SaveFailed");
            ShowError(error);
        }
        finally
        {
            FocusEditor();
        }
    }

    private static string FormatTranscript(IReadOnlyList<TranscriptLine> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.IsConsole)
            {
                text.Append(line.CopyText);
                continue;
            }
            if (text.Length > 0 && text[^1] is not ('\r' or '\n')) text.AppendLine();
            if (line.Prefix.Length > 0) text.Append(line.Prefix).Append(' ');
            text.AppendLine(line.CopyText);
        }
        return text.ToString();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowError(Exception error)
    {
        viewModel.Transcript.Add(TranscriptLine.Diagnostic(error.Message));
        MessageBox.Show(this, error.Message, viewModel.Localize("Dialog.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (quickInfoChordPending)
        {
            var showQuickInfo = e.Key == Key.I && Keyboard.Modifiers == ModifierKeys.Control;
            CancelQuickInfoChord();
            if (showQuickInfo)
            {
                e.Handled = true;
                await ShowQuickInfoAtCaretAsync();
                return;
            }
        }
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            BeginQuickInfoChord();
        }
        else if (assistMode == AssistMode.Completion &&
            e.Key is Key.Tab or Key.Enter && Keyboard.Modifiers == ModifierKeys.None &&
            CompletionList.SelectedItem is CompletionCandidate candidate)
        {
            e.Handled = true;
            await InsertCompletionAsync(candidate);
        }
        else if (assistMode != AssistMode.None && Keyboard.Modifiers == ModifierKeys.None &&
                 e.Key is Key.Up or Key.Down)
        {
            e.Handled = true;
            CompletionList.SelectedIndex = e.Key == Key.Down
                ? Math.Min(CompletionList.SelectedIndex + 1, CompletionList.Items.Count - 1)
                : Math.Max(CompletionList.SelectedIndex - 1, 0);
            CompletionList.ScrollIntoView(CompletionList.SelectedItem);
        }
        else if (assistMode != AssistMode.None && Keyboard.Modifiers == ModifierKeys.None &&
                 e.Key is Key.PageUp or Key.PageDown)
        {
            e.Handled = true;
            var current = Math.Max(0, CompletionList.SelectedIndex);
            var direction = e.Key == Key.PageDown ? 1 : -1;
            CompletionList.SelectedIndex = Math.Clamp(
                current + direction * GetCompletionPageSize(),
                0,
                CompletionList.Items.Count - 1);
            CompletionList.ScrollIntoView(CompletionList.SelectedItem);
        }
        else if (e.Key == Key.Enter && IsExecuteGesture(Keyboard.Modifiers))
        {
            e.Handled = true;
            HideAssist();
            var execution = viewModel.ExecuteAsync();
            Editor.Text = viewModel.InputText;
            Editor.CaretOffset = Editor.Text.Length;
            await execution;
            if (!closeInProgress && !string.Equals(Editor.Text, viewModel.InputText, StringComparison.Ordinal))
            {
                Editor.Text = viewModel.InputText;
                Editor.CaretOffset = Editor.Text.Length;
            }
        }
        else if (e.Key == Key.Enter && !IsLineBreakGesture(Keyboard.Modifiers))
        {
            e.Handled = true;
        }
        else if (e.Key == Key.Space && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await ShowCompletionAsync();
        }
        else if (e.Key == Key.Escape)
        {
            if (SymbolDetailPopup.IsOpen)
            {
                e.Handled = true;
                SymbolDetailPopup.IsOpen = false;
            }
            else if (QuickInfoPopup.IsOpen)
            {
                e.Handled = true;
                ClosePinnedQuickInfo();
            }
            else if (AssistPopup.IsOpen)
            {
                e.Handled = true;
                HideAssist();
            }
            else if (viewModel.CanCancel)
            {
                e.Handled = true;
                await CancelFromUiAsync();
            }
        }
        else if (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.None && CanNavigateHistory(up: true))
        {
            var value = viewModel.MoveHistory(-1, Editor.Text);
            if (value is not null)
            {
                e.Handled = true;
                Editor.Text = value;
                Editor.CaretOffset = Editor.Text.Length;
            }
        }
        else if (e.Key == Key.Down && Keyboard.Modifiers == ModifierKeys.None && CanNavigateHistory(up: false))
        {
            var value = viewModel.MoveHistory(1, Editor.Text);
            if (value is not null)
            {
                e.Handled = true;
                Editor.Text = value;
                Editor.CaretOffset = Editor.Text.Length;
            }
        }
    }

    private async Task CancelFromUiAsync()
    {
        try
        {
            await viewModel.CancelAsync();
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private bool CanNavigateHistory(bool up)
    {
        if (Editor.Document.LineCount == 1) return true;
        var line = Editor.Document.GetLineByOffset(Editor.CaretOffset).LineNumber;
        return up ? line == 1 : line == Editor.Document.LineCount;
    }

    private int GetCompletionPageSize()
    {
        if (CompletionList.Items.Count == 0) return 1;
        var index = Math.Max(0, CompletionList.SelectedIndex);
        if (CompletionList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container &&
            container.ActualHeight > 0 && CompletionList.ActualHeight > 0)
        {
            return Math.Max(1, (int)(CompletionList.ActualHeight / container.ActualHeight) - 1);
        }
        return 8;
    }

    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        viewModel.InputText = Editor.Text;
        ClosePinnedQuickInfo();
        if (assistMode == AssistMode.Signature) ScheduleSignatureHelpRefresh();
        else if (IsDynamicCommandCompletionStart(Editor.Text)) _ = ShowCompletionAsync();
        else if (assistMode == AssistMode.Completion) ApplyCompletionFilter();
    }

    private static bool IsDynamicCommandCompletionStart(string text) =>
        text is ":using add " or ":using remove ";

    private async Task ShowCompletionAsync()
    {
        ClosePinnedQuickInfo();
        CancelSignatureHelpRefresh();
        CancelCompletionRequest();
        assistMode = AssistMode.None;
        AssistPopup.IsOpen = false;
        var cancellation = new CancellationTokenSource();
        completionCancellation = cancellation;
        var requestText = Editor.Text;
        var requestOffset = Editor.CaretOffset;
        IReadOnlyList<CompletionCandidate> items;
        try
        {
            items = await viewModel.GetCompletionsAsync(requestOffset, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception error)
        {
            ShowAssistFailure(error);
            return;
        }
        var inputUnchanged = Editor.CaretOffset == requestOffset && Editor.Text == requestText;
        var inputAppendedAtEnd = requestOffset == requestText.Length &&
                                 Editor.CaretOffset == Editor.Text.Length &&
                                 Editor.Text.StartsWith(requestText, StringComparison.Ordinal);
        if (cancellation.IsCancellationRequested || !inputUnchanged && !inputAppendedAtEnd)
            return;
        allCompletionItems = items;
        lastCompletionFilterPrefix = null;
        completionFilterStart = requestText.TrimStart().StartsWith(':')
            ? 0
            : FindCompletionStart(requestText, requestOffset);
        CompletionList.IsHitTestVisible = true;
        CompletionList.DisplayMemberPath = null;
        CompletionList.ItemTemplate = (DataTemplate)FindResource("CompletionItemTemplate");
        assistMode = items.Count > 0 ? AssistMode.Completion : AssistMode.None;
        AssistHint.Text = viewModel.Localize("Assist.CompletionHint");
        ApplyCompletionFilter();
    }

    private async Task ShowSignatureHelpAsync()
    {
        ClosePinnedQuickInfo();
        CancelCompletionRequest();
        signatureHelpTimer.Stop();
        CancelAndDispose(ref signatureHelpCancellation);
        var cancellation = new CancellationTokenSource();
        signatureHelpCancellation = cancellation;
        var requestText = Editor.Text;
        var requestOffset = Editor.CaretOffset;
        SignatureHelpResult? help;
        try
        {
            help = await viewModel.GetSignatureHelpAsync(requestOffset, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception error)
        {
            ShowAssistFailure(error);
            return;
        }
        if (cancellation.IsCancellationRequested || requestText != Editor.Text || requestOffset != Editor.CaretOffset)
            return;
        if (help is null)
        {
            HideAssist();
            return;
        }
        assistMode = AssistMode.Signature;
        CompletionList.ItemsSource = help.Signatures;
        CompletionList.IsHitTestVisible = true;
        CompletionList.DisplayMemberPath = null;
        CompletionList.ItemTemplate = (DataTemplate)FindResource("SignatureItemTemplate");
        CompletionList.SelectedIndex = Math.Clamp(help.SelectedSignature, 0, help.Signatures.Count - 1);
        PrepareAssistPopupLayout();
        AssistPopup.IsOpen = true;
        UpdateAssistSummary();
    }

    private void ScheduleSignatureHelpRefresh()
    {
        signatureHelpCancellation?.Cancel();
        signatureHelpTimer.Stop();
        signatureHelpTimer.Start();
    }

    private void CancelSignatureHelpRefresh()
    {
        signatureHelpTimer.Stop();
        CancelAndDispose(ref signatureHelpCancellation);
    }

    private void CancelCompletionRequest()
    {
        CancelAndDispose(ref completionCancellation);
    }

    private async void CompletionList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (assistMode != AssistMode.Completion ||
            FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not
                CompletionCandidate item) return;

        e.Handled = true;
        await InsertCompletionAsync(item);
    }

    private void CompletionList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scrollViewer = FindDescendant<ScrollViewer>(CompletionList);
        if (scrollViewer is not { ScrollableHeight: > 0 }) return;

        var wheelNotches = Math.Max(1, Math.Abs(e.Delta) / 120);
        var linesPerNotch = SystemParameters.WheelScrollLines;
        if (linesPerNotch < 0)
        {
            for (var index = 0; index < wheelNotches; index++)
            {
                if (e.Delta > 0) scrollViewer.PageUp();
                else scrollViewer.PageDown();
            }
        }
        else
        {
            var lineCount = wheelNotches * Math.Clamp(linesPerNotch, 1, 10);
            for (var index = 0; index < lineCount; index++)
            {
                if (e.Delta > 0) scrollViewer.LineUp();
                else scrollViewer.LineDown();
            }
        }
        e.Handled = true;
    }

    private async Task InsertCompletionAsync(CompletionCandidate item)
    {
        var insertionText = item.TextToInsert;
        var replacementStart = Math.Clamp(item.ReplacementStart ?? completionFilterStart, 0, Editor.CaretOffset);
        var length = Editor.CaretOffset - replacementStart;
        if (item.RequiredNamespace is { } requiredNamespace)
        {
            try
            {
                if (await viewModel.AddUsingAsync(requiredNamespace))
                {
                    viewModel.SetLocalizedStatus("Status.AddedUsing", requiredNamespace);
                    viewModel.Transcript.Add(TranscriptLine.System(
                        viewModel.Localize("Message.UsingAutoAdded", requiredNamespace)));
                }
            }
            catch (Exception error)
            {
                viewModel.SetLocalizedStatus("Status.UsingFailed");
                viewModel.Transcript.Add(TranscriptLine.Diagnostic(error.Message));
                HideAssist();
                Editor.Focus();
                return;
            }
        }
        Editor.Document.Replace(replacementStart, length, insertionText);
        Editor.CaretOffset = replacementStart + insertionText.Length;
        HideAssist();
        Editor.Focus();
        ScheduleSignatureHelpRefresh();
    }

    private void HideAssist()
    {
        var wasCompletion = assistMode == AssistMode.Completion;
        completionDescriptionTimer.Stop();
        AssistPopup.IsOpen = false;
        CompletionList.ItemsSource = null;
        allCompletionItems = [];
        lastCompletionFilterPrefix = null;
        CancelCompletionRequest();
        CancelAndDispose(ref completionDescriptionCancellation);
        CancelSignatureHelpRefresh();
        AssistSummary.Text = string.Empty;
        AssistHint.Text = string.Empty;
        assistMode = AssistMode.None;
        if (!viewModel.IsRunning && wasCompletion) viewModel.SetLocalizedStatus("Status.Ready");
    }

    private void ApplyCompletionFilter()
    {
        if (assistMode != AssistMode.Completion) return;
        var caretOffset = Editor.CaretOffset;
        if (caretOffset < completionFilterStart || caretOffset > Editor.Text.Length)
        {
            HideAssist();
            return;
        }

        var prefix = Editor.Text[completionFilterStart..caretOffset];
        var isCommand = completionFilterStart == 0 && Editor.Text.StartsWith(':');
        if (!isCommand && prefix.Any(static character => !IsIdentifierPart(character)))
        {
            HideAssist();
            return;
        }
        if (string.Equals(lastCompletionFilterPrefix, prefix, StringComparison.Ordinal)) return;
        lastCompletionFilterPrefix = prefix;

        var filtered = allCompletionItems
            .Where(item => item.FilterText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        CompletionList.ItemsSource = filtered;
        CompletionList.SelectedIndex = filtered.Length > 0 ? 0 : -1;
        PrepareAssistPopupLayout();
        AssistPopup.IsOpen = filtered.Length > 0;
        if (filtered.Length == 0)
        {
            HideAssist();
            return;
        }
        viewModel.SetLocalizedStatus("Status.Completions", filtered.Length);
    }

    private void PrepareAssistPopupLayout()
    {
        var popupWidth = Math.Clamp(Editor.ActualWidth, 520, 780);
        AssistPopupBorder.Width = popupWidth;
        CompletionListColumn.Width = new(Math.Clamp(
            completionListWidth.Value,
            CompletionListColumn.MinWidth,
            popupWidth - 255));
    }

    private void CompletionSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (CompletionListColumn.ActualWidth >= CompletionListColumn.MinWidth)
            completionListWidth = new(CompletionListColumn.ActualWidth);
    }

    private void CompletionList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAssistSummary();

    private void UpdateAssistSummary()
    {
        completionDescriptionTimer.Stop();
        CancelAndDispose(ref completionDescriptionCancellation);

        if (assistMode == AssistMode.Signature && CompletionList.SelectedItem is SignatureInformation signature)
        {
            ShowSignatureDocumentation(signature);
            return;
        }
        if (assistMode != AssistMode.Completion || CompletionList.SelectedItem is not CompletionCandidate candidate)
        {
            ShowAssistSummary(null);
            return;
        }

        ShowAssistSummary(viewModel.Localize("Assist.LoadingDocumentation"));
        completionDescriptionTimer.Start();
    }

    private async Task LoadCompletionDescriptionAsync()
    {
        if (assistMode != AssistMode.Completion || CompletionList.SelectedItem is not CompletionCandidate candidate)
            return;

        var cancellation = new CancellationTokenSource();
        completionDescriptionCancellation = cancellation;
        try
        {
            var description = await viewModel.GetCompletionDescriptionAsync(Editor.CaretOffset, candidate, cancellation.Token);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(CompletionList.SelectedItem, candidate))
            {
                var importNotice = candidate.RequiredNamespace is null
                    ? null
                    : viewModel.Localize("Assist.AutoImport", candidate.RequiredNamespace);
                ShowAssistSummary(string.Join(
                    Environment.NewLine + Environment.NewLine,
                    new[] { importNotice, description }.Where(static text => !string.IsNullOrWhiteSpace(text))));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!cancellation.IsCancellationRequested) ShowAssistSummary(error.Message);
        }
        finally
        {
            if (ReferenceEquals(completionDescriptionCancellation, cancellation))
                completionDescriptionCancellation = null;
            cancellation.Dispose();
        }
    }

    private void ShowAssistFailure(Exception error)
    {
        HideAssist();
        viewModel.Transcript.Add(TranscriptLine.Diagnostic(error.Message));
        if (!viewModel.IsRunning) viewModel.SetLocalizedStatus("Status.Ready");
    }

    private void ShowAssistSummary(string? text)
    {
        AssistSummary.Text = string.IsNullOrWhiteSpace(text)
            ? viewModel.Localize("Assist.NoDocumentation")
            : text;
    }

    private void ShowSignatureDocumentation(SignatureInformation signature)
    {
        if (signature.ActiveParameter < 0 || signature.ActiveParameter >= signature.Parameters.Count)
        {
            AssistHint.Text = viewModel.Localize("Assist.SignatureHintNoParameter", CompletionList.Items.Count);
            ShowAssistSummary(signature.Summary);
            return;
        }

        var parameter = signature.Parameters[signature.ActiveParameter];
        AssistHint.Text = viewModel.Localize(
            "Assist.SignatureHint",
            CompletionList.Items.Count,
            signature.ActiveParameter + 1,
            signature.Parameters.Count,
            parameter.Name);
        var parameterDocumentation = string.IsNullOrWhiteSpace(parameter.Summary)
            ? viewModel.Localize("Assist.NoParameterDocumentation")
            : parameter.Summary;
        var parts = new[]
        {
            signature.Summary,
            viewModel.Localize("Assist.ActiveParameter", parameter.TypeName, parameter.Name),
            parameterDocumentation
        }.Where(static part => !string.IsNullOrWhiteSpace(part));
        ShowAssistSummary(string.Join(Environment.NewLine + Environment.NewLine, parts));
    }

    private static int FindCompletionStart(string text, int caretOffset)
    {
        var start = Math.Clamp(caretOffset, 0, text.Length);
        while (start > 0 && IsIdentifierPart(text[start - 1])) start--;
        return start;
    }

    private static bool IsIdentifierPart(char character) => char.IsLetterOrDigit(character) || character == '_';

    private bool IsExecuteGesture(ModifierKeys modifiers) => viewModel.ExecutionKeyMode switch
    {
        ExecutionKeyMode.Enter => modifiers == ModifierKeys.None,
        ExecutionKeyMode.ControlEnter => modifiers == ModifierKeys.Control,
        _ => false
    };

    private bool IsLineBreakGesture(ModifierKeys modifiers) => viewModel.ExecutionKeyMode switch
    {
        ExecutionKeyMode.Enter => modifiers == ModifierKeys.Shift,
        ExecutionKeyMode.ControlEnter => modifiers is ModifierKeys.None or ModifierKeys.Shift,
        _ => false
    };

    private void BeginQuickInfoChord()
    {
        quickInfoChordTimer.Stop();
        quickInfoChordStatusRestore?.Invoke();
        quickInfoChordStatusRestore = viewModel.CaptureStatusRestore();
        quickInfoChordPending = true;
        viewModel.SetStatusOverlay("Status.QuickInfoChord");
        quickInfoChordTimer.Start();
    }

    private void CancelQuickInfoChord()
    {
        quickInfoChordTimer.Stop();
        quickInfoChordPending = false;
        var restore = quickInfoChordStatusRestore;
        quickInfoChordStatusRestore = null;
        restore?.Invoke();
    }

    private async Task ShowQuickInfoAtCaretAsync()
    {
        HideAssist();
        ClosePinnedQuickInfo();
        var cancellation = new CancellationTokenSource();
        pinnedQuickInfoCancellation = cancellation;
        var requestText = Editor.Text;
        var requestOffset = Editor.CaretOffset;
        try
        {
            var result = await viewModel.GetQuickInfoAsync(requestOffset, cancellation.Token);
            if (cancellation.IsCancellationRequested || requestText != Editor.Text || requestOffset != Editor.CaretOffset)
                return;
            if (result is null)
            {
                ClosePinnedQuickInfo();
                viewModel.ShowStatusNotification("Status.QuickInfoUnavailable");
                return;
            }
            QuickInfoText.Text = result.Text;
            QuickInfoBorder.Width = Math.Clamp(Editor.ActualWidth, 360, 620);
            QuickInfoPopup.IsOpen = true;
            viewModel.ShowStatusNotification("Status.QuickInfoShown");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            if (!cancellation.IsCancellationRequested) ShowAssistFailure(error);
        }
        finally
        {
            if (ReferenceEquals(pinnedQuickInfoCancellation, cancellation))
                pinnedQuickInfoCancellation = null;
            cancellation.Dispose();
        }
    }

    private void ClosePinnedQuickInfo()
    {
        QuickInfoPopup.IsOpen = false;
        pinnedQuickInfoCancellation?.Cancel();
    }

    private async void Editor_MouseHover(object sender, MouseEventArgs e)
    {
        var position = Editor.GetPositionFromPoint(e.GetPosition(Editor));
        if (position is null) return;
        var offset = Editor.Document.GetOffset(position.Value.Location);
        CancelAndDispose(ref hoverQuickInfoCancellation);
        var cancellation = new CancellationTokenSource();
        hoverQuickInfoCancellation = cancellation;
        var requestText = Editor.Text;
        try
        {
            var result = await viewModel.GetQuickInfoAsync(offset, cancellation.Token);
            if (!cancellation.IsCancellationRequested && requestText == Editor.Text)
                Editor.ToolTip = result?.Text;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (!cancellation.IsCancellationRequested) Editor.ToolTip = null;
        }
        finally
        {
            if (ReferenceEquals(hoverQuickInfoCancellation, cancellation))
                hoverQuickInfoCancellation = null;
            cancellation.Dispose();
        }
    }

    private void Editor_MouseLeave(object sender, MouseEventArgs e)
    {
        CancelAndDispose(ref hoverQuickInfoCancellation);
        Editor.ToolTip = null;
    }

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        // Clear the field before cancellation. WPF can synchronously raise selection or
        // focus events while a popup is being torn down; those re-entrant handlers must
        // never observe a CancellationTokenSource that has already been disposed.
        var current = source;
        source = null;
        if (current is null) return;
        try
        {
            current.Cancel();
        }
        finally
        {
            current.Dispose();
        }
    }

    private void TranscriptLine_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock { DataContext: TranscriptLine { InputCode: not null } line })
        {
            Editor.Text = line.InputCode;
            Editor.CaretOffset = Editor.Text.Length;
            Editor.Focus();
        }
    }

    private void TranscriptRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: TranscriptLine line,
                ContextMenu: { } menu
            }) return;
        if (!line.IsCopyable && !line.IsSavable && !line.IsInspectable)
        {
            e.Handled = true;
            return;
        }
        menu.DataContext = line;
    }

    private void InspectResult_Click(object sender, RoutedEventArgs e)
    {
        if (GetTranscriptLine(sender)?.Snapshot is { } snapshot)
            OpenResultInspector(snapshot);
    }

    private async void CopyTranscriptLine_Click(object sender, RoutedEventArgs e)
    {
        if (GetTranscriptLine(sender) is not { } line) return;
        await CopyTextAsync(() => line.CopyText);
    }

    private async void CopyTranscriptLineJson_Click(object sender, RoutedEventArgs e)
    {
        if (GetTranscriptLine(sender)?.Snapshot is not { } snapshot) return;
        await CopyTextAsync(() => SnapshotJsonFormatter.Format(snapshot), "Status.CopiedJson");
    }

    private async void TranscriptTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None &&
            sender is TreeView { DataContext: TranscriptLine { Snapshot: { } snapshot } })
        {
            e.Handled = true;
            OpenResultInspector(snapshot);
            return;
        }
        if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control ||
            sender is not TreeView { SelectedItem: ConsoleSnapshotNode node }) return;
        e.Handled = true;
        await CopyTextAsync(() => node.CopyText);
    }

    private void TranscriptTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            sender is not TreeView { DataContext: TranscriptLine { Snapshot: { } snapshot } }) return;
        e.Handled = true;
        OpenResultInspector(snapshot);
    }

    private void OpenResultInspector(ResultSnapshot snapshot) =>
        new ResultInspectorWindow(snapshot, viewModel) { Owner = this }.Show();

    private void TranscriptTree_ExpansionChanged(object sender, RoutedEventArgs e)
    {
        // Expanding a structured result changes the ScrollViewer extent without adding
        // a transcript row. Re-evaluate sticky scrolling after layout so the jump button
        // appears when the active prompt is pushed out of view.
        Dispatcher.BeginInvoke(() => SetTranscriptAutoScroll(
                TranscriptScroll.ScrollableHeight - TranscriptScroll.VerticalOffset <= 2),
            DispatcherPriority.Loaded);
    }

    private async Task CopyTextAsync(Func<string> textFactory, string successStatus = "Status.Copied")
    {
        if (copyInProgress) return;
        copyInProgress = true;
        viewModel.SetStatusOverlay("Status.Copying");
        try
        {
            await ClipboardService.SetTextAsync(await Task.Run(textFactory));
            viewModel.ShowStatusNotification(successStatus);
        }
        catch (Exception error)
        {
            viewModel.ShowStatusNotification("Status.CopyFailed");
            ShowError(error);
        }
        finally
        {
            copyInProgress = false;
        }
    }

    private async void SaveTranscriptLine_Click(object sender, RoutedEventArgs e)
    {
        if (GetTranscriptLine(sender) is not { } line) return;
        var saveAsJson = line.Snapshot is not null;
        var dialog = new SaveFileDialog
        {
            Title = viewModel.Localize("Dialog.ResultSaveTitle"),
            Filter = viewModel.Localize(line.Snapshot is null ? "Dialog.TextFileFilter" : "Dialog.ResultFileFilter"),
            FilterIndex = saveAsJson ? 2 : 1,
            DefaultExt = saveAsJson ? ".json" : ".txt",
            AddExtension = true,
            FileName = $"PlayGroundSharp-result-{DateTime.Now:yyyyMMdd-HHmmss}{(saveAsJson ? ".json" : ".txt")}"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            viewModel.SetStatusOverlay("Status.SavingResult");
            var text = await Task.Run(() =>
                line.Snapshot is not null && Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase)
                    ? SnapshotJsonFormatter.Format(line.Snapshot)
                    : line.CopyText);
            await File.WriteAllTextAsync(dialog.FileName, text);
            viewModel.ShowStatusNotification("Status.Saved", Path.GetFileName(dialog.FileName));
        }
        catch (Exception error)
        {
            viewModel.ShowStatusNotification("Status.SaveFailed");
            ShowError(error);
        }
    }

    private static TranscriptLine? GetTranscriptLine(object sender)
    {
        if (sender is FrameworkElement { DataContext: TranscriptLine line }) return line;
        return sender is MenuItem menuItem &&
               ItemsControl.ItemsControlFromItemContainer(menuItem) is ContextMenu
               {
                   PlacementTarget: FrameworkElement { DataContext: TranscriptLine contextLine }
               }
            ? contextLine
            : null;
    }

    private void ConsoleSurface_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var control = FindAncestor<Control>(source);
        if (control is not null && !ReferenceEquals(control, TranscriptScroll)) return;
        Dispatcher.BeginInvoke(Editor.Focus);
    }

    private void TranscriptScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindAncestor<TreeView>(e.OriginalSource as DependencyObject) is null) return;
        var distance = Math.Clamp(TranscriptScroll.ViewportHeight * 0.08, 40, 100);
        TranscriptScroll.ScrollToVerticalOffset(Math.Clamp(
            TranscriptScroll.VerticalOffset - Math.Sign(e.Delta) * distance,
            0,
            TranscriptScroll.ScrollableHeight));
        e.Handled = true;
    }

    private void TranscriptScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Extent changes are caused by arriving output. Preserve the user's previous
        // sticky-scroll choice until they explicitly move the viewport.
        if (e.ExtentHeightChange == 0)
            SetTranscriptAutoScroll(TranscriptScroll.ScrollableHeight - TranscriptScroll.VerticalOffset <= 2);
        else
            UpdateScrollToLatestVisibility();
    }

    private void ScrollToLatest_Click(object sender, RoutedEventArgs e)
    {
        SetTranscriptAutoScroll(true);
        TranscriptScroll.ScrollToEnd();
        FocusEditor();
    }

    private void SetTranscriptAutoScroll(bool enabled)
    {
        transcriptAutoScroll = enabled;
        UpdateScrollToLatestVisibility();
    }

    private void UpdateScrollToLatestVisibility()
    {
        if (!IsLoaded) return;
        ScrollToLatestButton.Visibility = transcriptAutoScroll ? Visibility.Collapsed : Visibility.Visible;
    }

    private enum AssistMode { None, Completion, Signature }
}
