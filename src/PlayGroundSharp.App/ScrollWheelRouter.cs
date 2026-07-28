using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PlayGroundSharp.App;

/// <summary>Routes wheel gestures to horizontal scrolling surfaces across all app windows.</summary>
internal static class ScrollWheelRouter
{
    private static int isRegistered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref isRegistered, 1) != 0) return;
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel));
    }

    public static void ScrollHorizontally(ScrollViewer scrollViewer, int delta)
    {
        if (delta == 0) return;
        var distance = Math.Clamp(scrollViewer.ViewportWidth * 0.15, 36, 120);
        var offset = Math.Clamp(
            scrollViewer.HorizontalOffset + Math.Sign(delta) * distance,
            0,
            scrollViewer.ScrollableWidth);
        scrollViewer.ScrollToHorizontalOffset(offset);
    }

    public static bool TryRouteHorizontalWheel(
        DependencyObject scope,
        DependencyObject? originalSource,
        int delta,
        ModifierKeys modifiers,
        bool forceHorizontal = false)
    {
        var scrollViewer = scope as ScrollViewer ??
                           FindDescendant<ScrollViewer>(
                               scope,
                               static viewer => viewer.ScrollableWidth > 0);
        if (scrollViewer is not { ScrollableWidth: > 0 }) return false;

        var overHorizontalBar = FindAncestor<ScrollBar>(originalSource) is
            { Orientation: Orientation.Horizontal };
        var requestsHorizontal = forceHorizontal ||
                                 modifiers.HasFlag(ModifierKeys.Shift) ||
                                 scrollViewer.ScrollableHeight <= 0 ||
                                 overHorizontalBar;
        if (!requestsHorizontal) return false;

        ScrollHorizontally(scrollViewer, delta);
        return true;
    }

    public static bool IsOverHorizontalScrollZone(FrameworkElement element, Point position)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        var horizontalBarTop = element.ActualHeight - SystemParameters.HorizontalScrollBarHeight;
        var verticalBarLeft = element.ActualWidth - SystemParameters.VerticalScrollBarWidth;
        return position.Y >= horizontalBarTop &&
               position.Y <= element.ActualHeight &&
               position.X >= 0 &&
               position.X < verticalBarLeft;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer { ScrollableWidth: > 0 } scrollViewer ||
            !ReferenceEquals(FindAncestor<ScrollViewer>(e.OriginalSource as DependencyObject), scrollViewer))
            return;

        if (TryRouteHorizontalWheel(
                scrollViewer,
                e.OriginalSource as DependencyObject,
                e.Delta,
                Keyboard.Modifiers))
            e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = GetParent(current);
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject current, Func<T, bool> predicate)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(current);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(current, index);
            if (child is T match && predicate(match)) return match;
            if (FindDescendant<T>(child, predicate) is { } descendant) return descendant;
        }
        return null;
    }

    private static DependencyObject? GetParent(DependencyObject current)
    {
        if (current is ContentElement contentElement)
            return ContentOperations.GetParent(contentElement) ??
                   (contentElement as FrameworkContentElement)?.Parent;
        return current is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(current)
            : LogicalTreeHelper.GetParent(current);
    }
}
