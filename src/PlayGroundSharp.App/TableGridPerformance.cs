using System.Windows.Controls;

namespace PlayGroundSharp.App;

internal static class TableGridPerformance
{
    public const int DefaultCachedRowCount = 5_000;

    public static void Configure(
        DataGrid table,
        int cachedRowCount = DefaultCachedRowCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cachedRowCount);
        var cachedRowsBeforeViewport = cachedRowCount / 2;
        var cachedRowsAfterViewport = cachedRowCount - cachedRowsBeforeViewport;

        table.EnableRowVirtualization = true;
        table.EnableColumnVirtualization = true;
        table.SetValue(ScrollViewer.CanContentScrollProperty, true);
        table.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
        table.SetValue(
            VirtualizingPanel.VirtualizationModeProperty,
            VirtualizationMode.Recycling);
        table.SetValue(VirtualizingPanel.ScrollUnitProperty, ScrollUnit.Pixel);
        table.SetValue(
            VirtualizingPanel.CacheLengthProperty,
            new VirtualizationCacheLength(
                cachedRowsBeforeViewport,
                cachedRowsAfterViewport));
        table.SetValue(
            VirtualizingPanel.CacheLengthUnitProperty,
            VirtualizationCacheLengthUnit.Item);
        table.SetValue(ScrollViewer.IsDeferredScrollingEnabledProperty, false);
    }
}
