using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit.Highlighting;
using PlayGroundSharp.Core;
using PlayGroundSharp.LanguageService;

namespace PlayGroundSharp.App;

public partial class DataTypeInferenceDialog : Window
{
    private readonly ResultSnapshot snapshot;
    private readonly string sourceExpression;
    private readonly AppLanguageMode languageMode;
    private readonly Func<string, bool> variableExists;

    internal DataTypeInferenceDialog(
        ResultSnapshot snapshot,
        string sourceExpression,
        string suggestedTypeName,
        string suggestedVariableName,
        AppLanguageMode languageMode,
        Func<string, bool> variableExists)
    {
        this.snapshot = snapshot;
        this.sourceExpression = sourceExpression;
        this.languageMode = languageMode;
        this.variableExists = variableExists;
        InitializeComponent();
        PreviewText.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");
        TypeNameText.Text = suggestedTypeName;
        VariableNameText.Text = suggestedVariableName;
        UpdatePreview();
        Loaded += (_, _) =>
        {
            TypeNameText.Focus();
            TypeNameText.SelectAll();
        };
    }

    internal DataTypeInferenceResult? Result { get; private set; }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) UpdatePreview();
    }

    private void UpdatePreview()
    {
        Result = null;
        StatusText.Text = string.Empty;
        WarningText.Text = string.Empty;
        WarningBorder.Visibility = Visibility.Collapsed;
        var typeName = TypeNameText.Text.Trim();
        var variableName = VariableNameText.Text.Trim();
        if (!RetainedResultStatement.IsValidVariableName(typeName) || typeName.StartsWith('@'))
        {
            ShowError("DataInference.InvalidTypeName");
            return;
        }
        if (!RetainedResultStatement.IsValidVariableName(variableName))
        {
            ShowError("Variables.InvalidName");
            return;
        }
        if (variableExists(variableName))
        {
            StatusText.Text = AppLocalization.Text(languageMode, "Variables.NameExists", variableName);
            GenerateButton.IsEnabled = false;
            PreviewText.Text = string.Empty;
            return;
        }

        Result = DataTypeInference.Generate(snapshot, sourceExpression, typeName, variableName);
        if (Result is null)
        {
            ShowError("DataInference.Unsupported");
            return;
        }
        PreviewText.Text = Result.GeneratedCode;
        var warnings = Result.Warnings.Select(WarningKey)
            .Select(key => AppLocalization.Text(languageMode, key))
            .ToArray();
        if (warnings.Length > 0)
        {
            WarningText.Text = string.Join(Environment.NewLine, warnings.Select(message => $"• {message}"));
            WarningBorder.Visibility = Visibility.Visible;
        }
        GenerateButton.IsEnabled = true;
    }

    private void ShowError(string key)
    {
        StatusText.Text = AppLocalization.Text(languageMode, key);
        GenerateButton.IsEnabled = false;
        PreviewText.Text = string.Empty;
    }

    private static string WarningKey(DataTypeInferenceWarning warning) => warning switch
    {
        DataTypeInferenceWarning.TruncatedSnapshot => "DataInference.WarningTruncated",
        DataTypeInferenceWarning.FallbackType => "DataInference.WarningFallback",
        DataTypeInferenceWarning.EmptyCollection => "DataInference.WarningEmptyCollection",
        DataTypeInferenceWarning.UnreadableProperty => "DataInference.WarningUnreadableProperty",
        _ => "DataInference.WarningFallback"
    };

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        UpdatePreview();
        if (Result is not null) DialogResult = true;
    }
}
