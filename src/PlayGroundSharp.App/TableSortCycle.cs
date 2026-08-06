using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PlayGroundSharp.App;

internal enum TableSortState
{
    Original,
    Ascending,
    Descending
}

internal static class TableSortCycle
{
    public static TableSortState Next(TableSortState current) => current switch
    {
        TableSortState.Original => TableSortState.Ascending,
        TableSortState.Ascending => TableSortState.Descending,
        _ => TableSortState.Original
    };

    public static ListSortDirection? ToListSortDirection(TableSortState state) => state switch
    {
        TableSortState.Ascending => ListSortDirection.Ascending,
        TableSortState.Descending => ListSortDirection.Descending,
        _ => null
    };

    public static string Glyph(TableSortState state) => state switch
    {
        TableSortState.Ascending => "▲",
        TableSortState.Descending => "▼",
        _ => string.Empty
    };
}

internal sealed class TableSortHeader : Grid
{
    private readonly string exportName;

    public TableSortHeader(string exportName, string displayName)
    {
        this.exportName = exportName;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        ClipToBounds = true;
        ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        ColumnDefinitions.Add(new() { Width = GridLength.Auto });

        Label = new()
        {
            Text = displayName,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        SortGlyph = new()
        {
            Width = 12,
            Margin = new(6, 0, 0, 0),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        FilterGlyph = new()
        {
            Width = 10,
            Height = 10,
            Margin = new(6, 0, 0, 0),
            Data = Geometry.Parse("M0,0 L10,0 L6,5 L6,9 L4,7 L4,5 Z"),
            Stretch = Stretch.Uniform,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center
        };
        SetColumn(FilterGlyph, 1);
        SetColumn(SortGlyph, 2);
        Children.Add(Label);
        Children.Add(FilterGlyph);
        Children.Add(SortGlyph);
    }

    public TextBlock Label { get; }
    public Path FilterGlyph { get; }
    public TextBlock SortGlyph { get; }

    public override string ToString() => exportName;
}
