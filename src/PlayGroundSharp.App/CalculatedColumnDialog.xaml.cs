using System.Windows;
using System.Windows.Controls;

namespace PlayGroundSharp.App;

public partial class CalculatedColumnDialog : Window
{
    private readonly Func<string, string, string?> expressionFactory;
    private readonly Func<string, CancellationToken, Task<string?>> evaluator;
    private readonly AppLanguageMode languageMode;
    private CancellationTokenSource? evaluationCancellation;

    public CalculatedColumnDialog(
        AppLanguageMode languageMode,
        string suggestedName,
        string initialFormula,
        Func<string, string, string?> expressionFactory,
        Func<string, CancellationToken, Task<string?>> evaluator)
    {
        this.languageMode = languageMode;
        this.expressionFactory = expressionFactory;
        this.evaluator = evaluator;
        InitializeComponent();
        ColumnNameText.Text = suggestedName;
        FormulaText.Text = initialFormula;
        UpdateGeneratedExpression();
        Loaded += (_, _) =>
        {
            ColumnNameText.Focus();
            ColumnNameText.SelectAll();
        };
        Closed += (_, _) => evaluationCancellation?.Cancel();
    }

    public string? AppliedExpression { get; private set; }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        StatusText.Text = string.Empty;
        UpdateGeneratedExpression();
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
        ColumnNameText.IsEnabled = !evaluating;
        FormulaText.IsEnabled = !evaluating;
        ApplyButton.IsEnabled = !evaluating && GeneratedExpressionText.Text.Length > 0;
    }
}
