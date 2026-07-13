using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.Win32;
using PlayGroundSharp.Core;
using PlayGroundSharp.LanguageService;

namespace PlayGroundSharp.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel = new();
    private IReadOnlyList<CompletionCandidate> allCompletionItems = [];
    private CancellationTokenSource? completionDescriptionCancellation;
    private AssistMode assistMode;
    private int completionStart;
    private GridLength typeExplorerWidth = new(286);
    private GridLength referenceDrawerWidth = new(470);

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
            viewModel.UpdateCursorPosition(Editor.TextArea.Caret.Line, Editor.TextArea.Caret.Column);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await viewModel.InitializeAsync();
        Editor.Focus();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        completionDescriptionCancellation?.Cancel();
        completionDescriptionCancellation?.Dispose();
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

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
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
        else if (assistMode == AssistMode.Completion) ApplyCompletionFilter();
    }

    private async Task ShowCompletionAsync()
    {
        var requestText = Editor.Text;
        var requestOffset = Editor.CaretOffset;
        var items = await viewModel.GetCompletionsAsync(requestOffset);
        allCompletionItems = items;
        completionStart = items.Select(static item => item.ReplacementStart).Distinct().SingleOrDefault()
            ?? FindCompletionStart(requestText, requestOffset);
        CompletionList.IsHitTestVisible = true;
        CompletionList.DisplayMemberPath = nameof(CompletionCandidate.DisplayText);
        assistMode = items.Count > 0 ? AssistMode.Completion : AssistMode.None;
        AssistHint.Text = viewModel.Localize("Assist.CompletionHint");
        ApplyCompletionFilter();
    }

    private async Task ShowSignatureHelpAsync()
    {
        var help = await viewModel.GetSignatureHelpAsync(Editor.CaretOffset);
        if (help is null)
        {
            HideAssist();
            return;
        }
        var selectedSignature = CompletionList.SelectedItem as SignatureInformation;
        CompletionList.ItemsSource = help.Signatures;
        CompletionList.IsHitTestVisible = true;
        CompletionList.DisplayMemberPath = nameof(SignatureInformation.DisplayText);
        CompletionList.SelectedItem = help.Signatures.FirstOrDefault(item => item.DisplayText == selectedSignature?.DisplayText);
        if (CompletionList.SelectedIndex < 0) CompletionList.SelectedIndex = 0;
        AssistHint.Text = viewModel.Localize("Assist.SignatureHint", help.Signatures.Count, help.ActiveParameter + 1);
        AssistPopup.IsOpen = true;
        assistMode = AssistMode.Signature;
        UpdateAssistSummary();
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
        completionDescriptionCancellation?.Cancel();
        completionDescriptionCancellation?.Dispose();
        completionDescriptionCancellation = null;
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
            ShowAssistSummary(signature.Summary);
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
            new ResultInspectorWindow(snapshot) { Owner = this }.Show();
    }

    private void ConsoleSurface_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        Dispatcher.BeginInvoke(Editor.Focus);

    private enum AssistMode { None, Completion, Signature }
}
