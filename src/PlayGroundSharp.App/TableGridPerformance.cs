using System.Windows.Controls;

namespace PlayGroundSharp.App;

internal static class TableGridPerformance
{
    public const int MinimumCachedRowCount = 60;
    public const int DefaultCachedRowCount = 500;

    public static void Configure(
        DataGrid table,
        int rowCount,
        int maximumCachedRowCount = DefaultCachedRowCount)
    {
        var cachedRowCount = CalculateCachedRowCount(rowCount, maximumCachedRowCount);
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

    public static int CalculateCachedRowCount(
        int rowCount,
        int maximumCachedRowCount = DefaultCachedRowCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCachedRowCount);
        if (rowCount == 0 || maximumCachedRowCount == 0) return 0;

        var minimumForTable = Math.Min(MinimumCachedRowCount, rowCount);
        var proportionalCache = rowCount / 20;
        return Math.Min(
            maximumCachedRowCount,
            Math.Max(minimumForTable, proportionalCache));
    }
}
