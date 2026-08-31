using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using PlayGroundSharp.LanguageService;

namespace PlayGroundSharp.App;

public partial class CalculatedColumnDialog : Window
{
    private readonly Func<string, string, string?> expressionFactory;
    private readonly Func<string, CancellationToken, Task<string?>> evaluator;
    private readonly Func<string, int, CancellationToken, Task<IReadOnlyList<CompletionCandidate>>> completionProvider;
    private readonly AppLanguageMode languageMode;
    private CancellationTokenSource? evaluationCancellation;
    private CancellationTokenSource? completionCancellation;
    private CompletionWindow? completionWindow;

    public CalculatedColumnDialog(
        AppLanguageMode languageMode,
        string suggestedName,
        string initialFormula,
        Func<string, string, string?> expressionFactory,
        Func<string, CancellationToken, Task<string?>> evaluator,
        Func<string, int, CancellationToken, Task<IReadOnlyList<CompletionCandidate>>> completionProvider)
    {
        this.languageMode = languageMode;
        this.expressionFactory = expressionFactory;
        this.evaluator = evaluator;
        this.completionProvider = completionProvider;
        InitializeComponent();
        FormulaText.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");
        FormulaText.TextChanged += FormulaText_TextChanged;
        FormulaText.TextArea.TextEntered += FormulaText_TextEntered;
        FormulaText.PreviewKeyDown += FormulaText_PreviewKeyDown;
        ColumnNameText.Text = suggestedName;
        FormulaText.Text = initialFormula;
        UpdateGeneratedExpression();
        Loaded += (_, _) =>
        {
            ColumnNameText.Focus();
            ColumnNameText.SelectAll();
        };
        Closed += (_, _) =>
        {
            evaluationCancellation?.Cancel();
            CancelCompletionRequest();
            completionWindow?.Close();
        };
    }

    public string? AppliedExpression { get; private set; }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        StatusText.Text = string.Empty;
        UpdateGeneratedExpression();
    }

    private void FormulaText_TextChanged(object? sender, EventArgs e)
    {
        if (!IsInitialized) return;
        StatusText.Text = string.Empty;
        UpdateGeneratedExpression();
    }

    private async void FormulaText_TextEntered(object? sender, TextCompositionEventArgs e)
    {
        if (e.Text == ".") await ShowCompletionAsync();
    }

    private async void FormulaText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        e.Handled = true;
        await ShowCompletionAsync();
    }

    private async Task ShowCompletionAsync()
    {
        completionWindow?.Close();
        CancelCompletionRequest();
        var cancellation = new CancellationTokenSource();
        completionCancellation = cancellation;
        var requestText = FormulaText.Text;
        var requestOffset = FormulaText.CaretOffset;
        IReadOnlyList<CompletionCandidate> items;
        try
        {
            items = await completionProvider(requestText, requestOffset, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(completionCancellation, cancellation))
                completionCancellation = null;
            cancellation.Dispose();
        }

        var inputUnchanged = FormulaText.CaretOffset == requestOffset && FormulaText.Text == requestText;
        var inputAppendedAtEnd = requestOffset == requestText.Length &&
                                 FormulaText.CaretOffset == FormulaText.Text.Length &&
                                 FormulaText.Text.StartsWith(requestText, StringComparison.Ordinal);
        if (!inputUnchanged && !inputAppendedAtEnd || items.Count == 0) return;

        var window = new CompletionWindow(FormulaText.TextArea)
        {
            StartOffset = CompletionPlacement.FindStart(requestText, requestOffset, items)
        };
        foreach (var item in items)
            window.CompletionList.CompletionData.Add(new FormulaCompletionData(item));
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(completionWindow, window)) completionWindow = null;
        };
        completionWindow = window;
        window.Show();
    }

    private void CancelCompletionRequest()
    {
        var cancellation = completionCancellation;
        completionCancellation = null;
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void UpdateGeneratedExpression()
    {
        var name = ColumnNameText.Text.Trim();
        var formula = FormulaText.Text.Trim();
        var expression = expressionFactory(name, formula);
        GeneratedExpressionText.Text = expression ?? string.Empty;
        ApplyButton.IsEnabled = expression is not null && evaluationCancellation is null;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var expression = expressionFactory(ColumnNameText.Text.Trim(), FormulaText.Text.Trim());
        if (expression is null)
        {
            StatusText.Text = AppLocalization.Text(
                languageMode,
                "Inspector.CalculatedColumnInvalidInput");
            return;
        }

        evaluationCancellation = new();
        SetEvaluationState(true);
        try
        {
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            StatusText.Text = AppLocalization.Text(languageMode, "Inspector.CalculatedColumnEvaluating");
            var error = await evaluator(expression, evaluationCancellation.Token);
            if (error is not null)
            {
                StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
                StatusText.Text = error;
                return;
            }

            AppliedExpression = expression;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            StatusText.Text = AppLocalization.Text(languageMode, "Inspector.CalculatedColumnCancelled");
        }
        catch (Exception error)
        {
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
            StatusText.Text = error.Message;
        }
        finally
        {
            evaluationCancellation.Dispose();
            evaluationCancellation = null;
            if (IsVisible) SetEvaluationState(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) =>
        evaluationCancellation?.Cancel();

    private void SetEvaluationState(bool evaluating)
    {
        if (evaluating) completionWindow?.Close();
        ColumnNameText.IsEnabled = !evaluating;
        FormulaText.IsEnabled = !evaluating;
        ApplyButton.IsEnabled = !evaluating && GeneratedExpressionText.Text.Length > 0;
    }

    private sealed class FormulaCompletionData(CompletionCandidate candidate) : ICompletionData
    {
        public ImageSource? Image => null;
        public string Text => candidate.FilterText;
        public object Content => $"{candidate.KindGlyph}  {candidate.DisplayText}";
        public object? Description => candidate.HasNamespaceHint
            ? candidate.NamespaceDisplayText
            : null;
        public double Priority => 0;

        public void Complete(
            TextArea textArea,
            ISegment completionSegment,
            EventArgs insertionRequestEventArgs) =>
            textArea.Document.Replace(completionSegment, candidate.TextToInsert);
    }
}
