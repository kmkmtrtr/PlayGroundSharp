using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Highlighting;
using PlayGroundSharp.LanguageService;

namespace PlayGroundSharp.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel = new();
    private IReadOnlyList<CompletionCandidate> allCompletionItems = [];
    private CancellationTokenSource? completionDescriptionCancellation;
    private AssistMode assistMode;
    private int completionStart;

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

    private void TypeExplorerTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        viewModel.SelectedExplorerNode = e.NewValue as SymbolExplorerNode;

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
            if (AssistPopup.IsOpen) HideAssist();
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
        AssistSummaryPanel.Visibility = Visibility.Collapsed;
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
        AssistSummary.Text = text ?? string.Empty;
        AssistSummaryPanel.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
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
