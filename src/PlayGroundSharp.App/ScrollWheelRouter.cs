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

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer { ScrollableWidth: > 0 } scrollViewer ||
            !ReferenceEquals(FindAncestor<ScrollViewer>(e.OriginalSource as DependencyObject), scrollViewer))
            return;

        var overHorizontalBar = FindAncestor<ScrollBar>(e.OriginalSource as DependencyObject) is
            { Orientation: Orientation.Horizontal };
        var requestsHorizontal = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ||
                                 scrollViewer.ScrollableHeight <= 0 ||
                                 overHorizontalBar;
        if (!requestsHorizontal) return;

        ScrollHorizontally(scrollViewer, -e.Delta);
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
