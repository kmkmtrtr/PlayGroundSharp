using System.ComponentModel;
using System.Windows.Controls;

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

internal sealed class TableSortHeader(string exportName) : StackPanel
{
    public override string ToString() => exportName;
}
