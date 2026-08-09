using System.Windows;
using System.Windows.Controls;

namespace PlayGroundSharp.App;

internal partial class ColumnFilterDialog : Window
{
    public ColumnFilterDialog(
        AppLanguageMode languageMode,
        string columnName,
        TableColumnFilter? currentFilter)
    {
        InitializeComponent();
        ColumnNameText.Text = columnName;
        OperatorCombo.ItemsSource = Enum.GetValues<TableFilterOperator>()
            .Select(value => new FilterOperatorOption(
                value,
                AppLocalization.Text(languageMode, $"Inspector.FilterOperator.{value}")))
            .ToArray();
        OperatorCombo.SelectedIndex = currentFilter is null
            ? 0
            : Array.IndexOf(Enum.GetValues<TableFilterOperator>(), currentFilter.Operator);
        ValueText.Text = currentFilter?.Value ?? string.Empty;
        ClearButton.IsEnabled = currentFilter is not null;
        Loaded += (_, _) =>
        {
            if (ValueText.IsEnabled)
            {
                ValueText.Focus();
                ValueText.SelectAll();
            }
            else OperatorCombo.Focus();
        };
    }

    public TableColumnFilter? AppliedFilter { get; private set; }
    public bool WasCleared { get; private set; }

    private void OperatorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var requiresValue = SelectedOperator is { } value &&
            value is not TableFilterOperator.IsEmpty and not TableFilterOperator.IsNotEmpty;
        ValueText.IsEnabled = requiresValue;
    }

    private TableFilterOperator? SelectedOperator =>
        (OperatorCombo.SelectedItem as FilterOperatorOption)?.Value;

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedOperator is not { } filterOperator) return;
        AppliedFilter = new(filterOperator, ValueText.Text);
        DialogResult = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        WasCleared = true;
        DialogResult = true;
    }

    private sealed record FilterOperatorOption(TableFilterOperator Value, string Label)
    {
        public override string ToString() => Label;
    }
}
