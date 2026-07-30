using System.Windows.Controls;

namespace PlayGroundSharp.App;

internal static class TableGridPerformance
{
    public const int InitialCachedRowCount = 20;
    public const int MinimumCachedRowCount = 60;
    public const int DefaultCachedRowCount = 500;
    public const int CacheWarmupStep = 20;

    public static void Configure(
        DataGrid table,
        int rowCount,
        int maximumCachedRowCount = DefaultCachedRowCount)
    {
        var cachedRowCount = CalculateCachedRowCount(rowCount, maximumCachedRowCount);
        table.EnableRowVirtualization = true;
        table.EnableColumnVirtualization = true;
        table.SetValue(ScrollViewer.CanContentScrollProperty, true);
        table.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
        table.SetValue(
            VirtualizingPanel.VirtualizationModeProperty,
            VirtualizationMode.Recycling);
        table.SetValue(VirtualizingPanel.ScrollUnitProperty, ScrollUnit.Pixel);
        SetCachedRowCount(table, cachedRowCount);
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

    public static int NextCachedRowCount(int current, int target)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(current);
        ArgumentOutOfRangeException.ThrowIfNegative(target);
        if (current >= target) return target;
        return (int)Math.Min((long)target, (long)current + CacheWarmupStep);
    }

    public static void SetCachedRowCount(DataGrid table, int cachedRowCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cachedRowCount);
        var cachedRowsBeforeViewport = cachedRowCount / 2;
        var cachedRowsAfterViewport = cachedRowCount - cachedRowsBeforeViewport;
        table.SetValue(
            VirtualizingPanel.CacheLengthProperty,
            new VirtualizationCacheLength(
                cachedRowsBeforeViewport,
                cachedRowsAfterViewport));
        table.SetValue(
            VirtualizingPanel.CacheLengthUnitProperty,
            VirtualizationCacheLengthUnit.Item);
    }
}
