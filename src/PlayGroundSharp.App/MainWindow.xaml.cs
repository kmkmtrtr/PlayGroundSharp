using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
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
    private readonly MainViewModel viewModel = new();
    private IReadOnlyList<CompletionCandidate> allCompletionItems = [];
    private CancellationTokenSource? completionCancellation;
    private CancellationTokenSource? completionDescriptionCancellation;
    private CancellationTokenSource? signatureHelpCancellation;
    private readonly DispatcherTimer signatureHelpTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private AssistMode assistMode;
    private int completionStart;
    private GridLength typeExplorerWidth = new(286);
    private GridLength referenceDrawerWidth = new(470);
    private HwndSource? windowSource;

    public MainWindow()
    {
        App.ApplyLanguage(viewModel.LanguageMode);
        InitializeComponent();
        DataContext = viewModel;
        App.ApplyTheme(viewModel.ThemeMode);
        Editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");
        viewModel.Transcript.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(TranscriptScroll.ScrollToEnd);
        Editor.TextArea.TextEntered += async (_, args) =>
        {
            if (args.Text == ":") await ShowCompletionAsync();
            if (args.Text == ".") await ShowCompletionAsync();
            if (args.Text == "(" || args.Text == "," && assistMode == AssistMode.Signature)
                await ShowSignatureHelpAsync();
            if (args.Text == ")" && assistMode == AssistMode.Signature) HideAssist();
        };
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            viewModel.UpdateCursorPosition(Editor.TextArea.Caret.Line, Editor.TextArea.Caret.Column);
            if (assistMode == AssistMode.Signature) ScheduleSignatureHelpRefresh();
        };
        signatureHelpTimer.Tick += async (_, _) =>
        {
            signatureHelpTimer.Stop();
            await ShowSignatureHelpAsync();
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        windowSource?.AddHook(WindowMessageHook);
        await viewModel.InitializeAsync();
        Editor.Focus();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        windowSource?.RemoveHook(WindowMessageHook);
        windowSource = null;
        completionCancellation?.Cancel();
        completionCancellation?.Dispose();
        completionDescriptionCancellation?.Cancel();
        completionDescriptionCancellation?.Dispose();
        signatureHelpCancellation?.Cancel();
        signatureHelpCancellation?.Dispose();
        await viewModel.DisposeAsync();
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
            ReferenceDrawerSplitterColumn.Width = new(5);
            ReferenceDrawerColumn.Width = referenceDrawerWidth;
            return;
        }
        if (ReferenceDrawerColumn.ActualWidth > 0) referenceDrawerWidth = new(ReferenceDrawerColumn.ActualWidth);
        ReferenceDrawerSplitterColumn.Width = new(0);
        ReferenceDrawerColumn.Width = new(0);
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (SymbolDetailPopup.IsOpen &&
            FindAncestor<TreeView>(e.OriginalSource as DependencyObject) != TypeExplorerTree)
            SymbolDetailPopup.IsOpen = false;
    }

    private void Window_Deactivated(object? sender, EventArgs e) => SymbolDetailPopup.IsOpen = false;

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
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
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
        if (e.Key == Key.F1)
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
    }

    private async void SaveWorkspace_Click(object sender, RoutedEventArgs e) => await SaveWorkspaceAsync();

    private async Task SaveWorkspaceAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = viewModel.Localize("Menu.SaveWorkspace"),
            Filter = viewModel.Localize("Dialog.WorkspaceFilter"),
            DefaultExt = ".pgsworkspace",
            AddExtension = true,
            FileName = $"PlayGroundSharp-{DateTime.Now:yyyyMMdd-HHmm}.pgsworkspace"
        };
        if (dialog.ShowDialog(this) != true) return;
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
    }

    private async void OpenWorkspace_Click(object sender, RoutedEventArgs e) => await OpenWorkspaceAsync();

    private async Task OpenWorkspaceAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = viewModel.Localize("Dialog.WorkspaceLoadTitle"),
            Filter = viewModel.Localize("Dialog.WorkspaceFilter"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        if (MessageBox.Show(this, viewModel.Localize("Dialog.WorkspaceLoadWarning"),
                viewModel.Localize("Dialog.WorkspaceLoadTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) !=
            MessageBoxResult.OK) return;
        try
        {
            var document = await WorkspaceFile.LoadAsync(dialog.FileName);
            await viewModel.LoadWorkspaceAsync(document);
            Editor.Text = viewModel.InputText;
            Editor.CaretOffset = Editor.Text.Length;
            FocusEditor();
        }
        catch (Exception error)
        {
            ShowError(error);
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
        if (dialog.ShowDialog(this) != true) return;
        var path = dialog.FileName.Replace("\"", "\"\"");
        Editor.Text = operation switch
        {
            "Inspect" => $"Data.Inspect(@\"{path}\")",
            "Preview" => $"Data.PreviewText(@\"{path}\", 65536)",
            "Lines" => $"Data.ReadLines(@\"{path}\").Take(100)",
            "Json" => $"await Data.ReadJsonAsync(@\"{path}\")",
            "JsonArray" => $"await Data.ReadJsonArrayAsync(@\"{path}\", take: 100)",
            "JsonLines" => $"var rows = new List<JsonElement>();{Environment.NewLine}" +
                           $"await foreach (var row in Data.ReadJsonLinesAsync(@\"{path}\")){Environment.NewLine}" +
                           $"{{{Environment.NewLine}    rows.Add(row);{Environment.NewLine}" +
                           $"    if (rows.Count == 100) break;{Environment.NewLine}}}{Environment.NewLine}rows",
            _ => Editor.Text
        };
        Editor.CaretOffset = Editor.Text.Length;
        FocusEditor();
    }

    private void Editor_PreviewDragOver(object sender, DragEventArgs e)
    {
        var canDrop = TryGetDroppedPaths(e.Data, out _);
        e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = canDrop ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void Editor_PreviewDragLeave(object sender, DragEventArgs e) =>
        DropOverlay.Visibility = Visibility.Collapsed;

    private void Editor_PreviewDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!TryGetDroppedPaths(e.Data, out var paths))
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
        menu.IsOpen = true;
        viewModel.SetLocalizedStatus("Status.DropChooseAction");
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
            menu.Items.Add(CreateDropAction("Drop.FileInfo", $"Data.Inspect({literal})"));
            var extension = Path.GetExtension(path);
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                menu.Items.Add(CreateDropAction("Drop.ReadJson", $"await Data.ReadJsonAsync({literal})"));
                menu.Items.Add(CreateDropAction("Drop.ReadJsonArray", $"await Data.ReadJsonArrayAsync({literal}, take: 100)"));
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

        menu.IsOpen = true;
        viewModel.SetLocalizedStatus("Status.DropChooseAction");
    }

    private MenuItem CreateDropAction(string localizationKey, string snippet)
    {
        var item = new MenuItem { Header = viewModel.Localize(localizationKey) };
        item.Click += (_, _) =>
        {
            InsertDroppedSnippet(snippet);
            viewModel.SetLocalizedStatus("Status.DroppedPath");
        };
        return item;
    }

    private void InsertDroppedSnippet(string snippet)
    {
        var start = Editor.SelectionStart;
        Editor.Document.Replace(start, Editor.SelectionLength, snippet);
        Editor.CaretOffset = start + snippet.Length;
        Editor.Focus();
    }

    private static bool TryGetDroppedPaths(IDataObject data, out string[] paths)
    {
        paths = data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] dropped
            ? dropped.Where(static path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        return paths.Length > 0;
    }

    private static bool IsTextFile(string extension) => extension.ToLowerInvariant() is
        ".txt" or ".log" or ".csv" or ".tsv" or ".md" or ".xml" or ".yaml" or ".yml" or
        ".json" or ".jsonl" or ".ndjson" or ".cs" or ".csx";

    private void OpenSymbolDocumentation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SymbolExplorerNode { DocumentationPath: { } path } }) return;
        var locale = viewModel.LanguageMode == AppLanguageMode.Japanese ? "ja-jp" : "en-us";
        Process.Start(new ProcessStartInfo($"https://learn.microsoft.com/{locale}/dotnet/api/{path}?view=net-10.0")
        {
            UseShellExecute = true
        });
    }

    private void OpenHelp_Click(object sender, RoutedEventArgs e) => OpenHelp();

    private void OpenHelp() => new HelpWindow(viewModel.LanguageMode) { Owner = this }.Show();

    private void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, viewModel.Localize("About.Message"), "PlayGroundSharp", MessageBoxButton.OK, MessageBoxImage.Information);

    private void FocusInput_Click(object sender, RoutedEventArgs e) => FocusEditor();

    private async void RemoveUsing_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: string @namespace }) return;
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

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowError(Exception error)
    {
        viewModel.Transcript.Add(TranscriptLine.Diagnostic(error.Message));
        MessageBox.Show(this, error.Message, viewModel.Localize("Dialog.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (assistMode == AssistMode.Completion && e.Key == Key.Tab &&
            CompletionList.SelectedItem is CompletionCandidate candidate)
        {
            e.Handled = true;
            await InsertCompletionAsync(candidate);
        }
        else if (assistMode != AssistMode.None && e.Key is Key.Up or Key.Down)
        {
            e.Handled = true;
            CompletionList.SelectedIndex = e.Key == Key.Down
                ? Math.Min(CompletionList.SelectedIndex + 1, CompletionList.Items.Count - 1)
                : CompletionList.SelectedIndex <= 0 ? CompletionList.Items.Count - 1 : CompletionList.SelectedIndex - 1;
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
            else if (AssistPopup.IsOpen) HideAssist();
            else if (viewModel.IsRunning) await viewModel.CancelAsync();
        }
        else if (e.Key == Key.Up && Editor.Document.LineCount == 1)
        {
            var value = viewModel.MoveHistory(-1);
            if (value is not null) Editor.Text = value;
        }
        else if (e.Key == Key.Down && Editor.Document.LineCount == 1)
        {
            var value = viewModel.MoveHistory(1);
            if (value is not null) Editor.Text = value;
        }
    }

    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        viewModel.InputText = Editor.Text;
        if (assistMode == AssistMode.Signature && !HasOpenArgumentList(Editor.Text, Editor.CaretOffset)) HideAssist();
        else if (assistMode == AssistMode.Signature) ScheduleSignatureHelpRefresh();
        else if (assistMode == AssistMode.Completion) ApplyCompletionFilter();
    }

    private async Task ShowCompletionAsync()
    {
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
        if (cancellation.IsCancellationRequested ||
            Editor.CaretOffset < requestOffset ||
            !Editor.Text.StartsWith(requestText, StringComparison.Ordinal))
            return;
        allCompletionItems = items;
        completionStart = items.Select(static item => item.ReplacementStart).Distinct().SingleOrDefault()
            ?? FindCompletionStart(requestText, requestOffset);
        CompletionList.IsHitTestVisible = true;
        CompletionList.DisplayMemberPath = null;
        CompletionList.ItemTemplate = (DataTemplate)FindResource("CompletionItemTemplate");
        assistMode = items.Count > 0 ? AssistMode.Completion : AssistMode.None;
        AssistHint.Text = viewModel.Localize("Assist.CompletionHint");
        ApplyCompletionFilter();
    }

    private async Task ShowSignatureHelpAsync()
    {
        CancelCompletionRequest();
        signatureHelpTimer.Stop();
        signatureHelpCancellation?.Cancel();
        signatureHelpCancellation?.Dispose();
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
        signatureHelpCancellation?.Cancel();
        signatureHelpCancellation?.Dispose();
        signatureHelpCancellation = null;
    }

    private void CancelCompletionRequest()
    {
        completionCancellation?.Cancel();
        completionCancellation?.Dispose();
        completionCancellation = null;
    }

    private async void CompletionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CompletionList.SelectedItem is CompletionCandidate item)
        {
            await InsertCompletionAsync(item);
        }
    }

    private async Task InsertCompletionAsync(CompletionCandidate item)
    {
        var insertionText = item.TextToInsert;
        var length = Math.Max(0, Editor.CaretOffset - completionStart);
        Editor.Document.Replace(completionStart, length, insertionText);
        Editor.CaretOffset = completionStart + insertionText.Length;
        HideAssist();
        if (item.RequiredNamespace is { } requiredNamespace)
        {
            try
            {
                if (await viewModel.AddUsingAsync(requiredNamespace))
                    viewModel.SetLocalizedStatus("Status.AddedUsing", requiredNamespace);
            }
            catch (Exception error)
            {
                viewModel.SetLocalizedStatus("Status.UsingFailed");
                viewModel.Transcript.Add(TranscriptLine.Diagnostic(error.Message));
            }
        }
        Editor.Focus();
    }

    private void HideAssist()
    {
        var wasCompletion = assistMode == AssistMode.Completion;
        AssistPopup.IsOpen = false;
        CompletionList.ItemsSource = null;
        allCompletionItems = [];
        CancelCompletionRequest();
        completionDescriptionCancellation?.Cancel();
        completionDescriptionCancellation?.Dispose();
        completionDescriptionCancellation = null;
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
        if (caretOffset < completionStart || caretOffset > Editor.Text.Length)
        {
            HideAssist();
            return;
        }

        var prefix = Editor.Text[completionStart..caretOffset];
        var isCommand = completionStart == 0 && Editor.Text.StartsWith(':');
        if (!isCommand && prefix.Any(static character => !IsIdentifierPart(character)))
        {
            HideAssist();
            return;
        }

        var filtered = allCompletionItems
            .Where(item => item.FilterText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        CompletionList.ItemsSource = filtered;
        CompletionList.SelectedIndex = filtered.Length > 0 ? 0 : -1;
        AssistPopup.IsOpen = filtered.Length > 0;
        if (filtered.Length == 0)
        {
            HideAssist();
            return;
        }
        viewModel.SetLocalizedStatus("Status.Completions", filtered.Length);
    }

    private void CompletionList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAssistSummary();

    private async void UpdateAssistSummary()
    {
        completionDescriptionCancellation?.Cancel();
        completionDescriptionCancellation?.Dispose();
        completionDescriptionCancellation = null;

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

        var cancellation = new CancellationTokenSource();
        completionDescriptionCancellation = cancellation;
        ShowAssistSummary(viewModel.Localize("Assist.LoadingDocumentation"));
        try
        {
            var description = await viewModel.GetCompletionDescriptionAsync(Editor.CaretOffset, candidate, cancellation.Token);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(CompletionList.SelectedItem, candidate))
                ShowAssistSummary(description);
        }
        catch (OperationCanceledException) { }
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
        ExecutionKeyMode.ControlEnter => modifiers == ModifierKeys.None,
        _ => false
    };

    private static bool HasOpenArgumentList(string text, int caretOffset)
    {
        var balance = 0;
        foreach (var character in text.AsSpan(0, Math.Clamp(caretOffset, 0, text.Length)))
        {
            if (character == '(') balance++;
            else if (character == ')') balance--;
        }
        return balance > 0;
    }

    private async void Editor_MouseHover(object sender, MouseEventArgs e)
    {
        var position = Editor.GetPositionFromPoint(e.GetPosition(Editor));
        if (position is null) return;
        var offset = Editor.Document.GetOffset(position.Value.Location);
        Editor.ToolTip = (await viewModel.GetQuickInfoAsync(offset))?.Text;
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

    private void InspectResult_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TranscriptLine { Snapshot: { } snapshot } })
            new ResultInspectorWindow(snapshot, viewModel.LanguageMode) { Owner = this }.Show();
    }

    private async void CopyTranscriptLine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TranscriptLine line }) return;
        try
        {
            Clipboard.SetText(await Task.Run(() => line.CopyText));
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private async void SaveTranscriptLine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TranscriptLine line }) return;
        var dialog = new SaveFileDialog
        {
            Title = viewModel.Localize("Dialog.ResultSaveTitle"),
            Filter = viewModel.Localize("Dialog.ResultFileFilter"),
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = $"PlayGroundSharp-result-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var text = await Task.Run(() => line.CopyText);
            await File.WriteAllTextAsync(dialog.FileName, text);
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private void ConsoleSurface_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        Dispatcher.BeginInvoke(Editor.Focus);

    private enum AssistMode { None, Completion, Signature }
}
