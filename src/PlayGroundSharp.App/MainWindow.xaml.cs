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
    private AssistMode assistMode;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
        Editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");
        viewModel.Transcript.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(TranscriptScroll.ScrollToEnd);
        Editor.TextArea.TextEntered += async (_, args) =>
        {
            if (args.Text == ".") await ShowCompletionAsync();
            if (args.Text == "(") await ShowSignatureHelpAsync();
            if (args.Text == ")" && assistMode == AssistMode.Signature) HideAssist();
        };
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
            viewModel.CursorStatus = $"Ln {Editor.TextArea.Caret.Line}, Col {Editor.TextArea.Caret.Column}";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await viewModel.InitializeAsync();
        Editor.Focus();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e) => await viewModel.DisposeAsync();

    private async void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (assistMode == AssistMode.Completion && e.Key is Key.Enter or Key.Tab &&
            CompletionList.SelectedItem is CompletionCandidate candidate)
        {
            e.Handled = true;
            InsertCompletion(candidate);
        }
        else if (assistMode == AssistMode.Completion && e.Key is Key.Up or Key.Down)
        {
            e.Handled = true;
            CompletionList.SelectedIndex = Math.Clamp(
                CompletionList.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, CompletionList.Items.Count - 1);
            CompletionList.ScrollIntoView(CompletionList.SelectedItem);
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            HideAssist();
            await viewModel.ExecuteAsync();
            Editor.Text = viewModel.InputText;
        }
        else if (e.Key == Key.Space && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await ShowCompletionAsync();
        }
        else if (e.Key == Key.Escape)
        {
            if (CompletionPanel.Visibility == Visibility.Visible) HideAssist();
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
    }

    private async Task ShowCompletionAsync()
    {
        var items = await viewModel.GetCompletionsAsync(Editor.CaretOffset);
        CompletionList.ItemsSource = items;
        CompletionList.IsHitTestVisible = true;
        CompletionList.DisplayMemberPath = nameof(CompletionCandidate.DisplayText);
        CompletionList.SelectedIndex = items.Count > 0 ? 0 : -1;
        CompletionPanel.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        assistMode = items.Count > 0 ? AssistMode.Completion : AssistMode.None;
        viewModel.Status = items.Count > 0 ? $"{items.Count} completions" : "No completions";
    }

    private async Task ShowSignatureHelpAsync()
    {
        var help = await viewModel.GetSignatureHelpAsync(Editor.CaretOffset);
        if (help is null)
        {
            HideAssist();
            return;
        }
        CompletionList.ItemsSource = help.Signatures;
        CompletionList.IsHitTestVisible = false;
        CompletionList.DisplayMemberPath = string.Empty;
        CompletionList.SelectedIndex = -1;
        CompletionPanel.Visibility = Visibility.Visible;
        assistMode = AssistMode.Signature;
    }

    private void CompletionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CompletionList.SelectedItem is CompletionCandidate item)
        {
            InsertCompletion(item);
        }
    }

    private void InsertCompletion(CompletionCandidate item)
    {
        Editor.Document.Insert(Editor.CaretOffset, item.DisplayText);
        HideAssist();
        Editor.Focus();
    }

    private void HideAssist()
    {
        CompletionPanel.Visibility = Visibility.Collapsed;
        CompletionList.ItemsSource = null;
        assistMode = AssistMode.None;
    }

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

    private enum AssistMode { None, Completion, Signature }
}
