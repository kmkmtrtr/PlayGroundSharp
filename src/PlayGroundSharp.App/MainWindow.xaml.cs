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
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            CompletionPopup.IsOpen = false;
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
            if (CompletionPopup.IsOpen) CompletionPopup.IsOpen = false;
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

    private void Editor_TextChanged(object? sender, EventArgs e) => viewModel.InputText = Editor.Text;

    private async Task ShowCompletionAsync()
    {
        var items = await viewModel.GetCompletionsAsync(Editor.CaretOffset);
        CompletionList.ItemsSource = items;
        CompletionList.DisplayMemberPath = nameof(CompletionCandidate.DisplayText);
        CompletionList.SelectedIndex = items.Count > 0 ? 0 : -1;
        CompletionPopup.IsOpen = items.Count > 0;
    }

    private async Task ShowSignatureHelpAsync()
    {
        var help = await viewModel.GetSignatureHelpAsync(Editor.CaretOffset);
        if (help is null) return;
        CompletionList.ItemsSource = help.Signatures;
        CompletionList.DisplayMemberPath = string.Empty;
        CompletionList.SelectedIndex = 0;
        CompletionPopup.IsOpen = true;
    }

    private void CompletionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CompletionList.SelectedItem is CompletionCandidate item)
        {
            Editor.Document.Insert(Editor.CaretOffset, item.DisplayText);
            CompletionPopup.IsOpen = false;
            Editor.Focus();
        }
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
}
