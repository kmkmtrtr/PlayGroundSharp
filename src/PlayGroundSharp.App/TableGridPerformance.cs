using System.Windows.Controls;

namespace PlayGroundSharp.App;

internal static class TableGridPerformance
{
    public static void Configure(DataGrid table)
    {
        table.EnableRowVirtualization = true;
        table.EnableColumnVirtualization = true;
        table.SetValue(ScrollViewer.CanContentScrollProperty, true);
        table.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
        table.SetValue(
            VirtualizingPanel.VirtualizationModeProperty,
            VirtualizationMode.Recycling);
        table.SetValue(VirtualizingPanel.ScrollUnitProperty, ScrollUnit.Item);
        table.SetValue(
            VirtualizingPanel.CacheLengthProperty,
            new VirtualizationCacheLength(1, 1));
        table.SetValue(
            VirtualizingPanel.CacheLengthUnitProperty,
            VirtualizationCacheLengthUnit.Page);
        table.SetValue(ScrollViewer.IsDeferredScrollingEnabledProperty, true);
    }
}
