using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PlayGroundSharp.App.Tests;

public sealed class ScrollWheelRouterTests
{
    [Fact]
    public void LargeTableUsesPixelScrollingWithAFiveHundredRowCache()
    {
        RunOnStaThread(() =>
        {
            var table = new DataGrid();

            TableGridPerformance.Configure(table);

            Assert.True(table.EnableRowVirtualization);
            Assert.True(table.EnableColumnVirtualization);
            Assert.True(ScrollViewer.GetCanContentScroll(table));
            Assert.True(VirtualizingPanel.GetIsVirtualizing(table));
            Assert.Equal(
                VirtualizationMode.Recycling,
                VirtualizingPanel.GetVirtualizationMode(table));
            Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(table));
            Assert.Equal(
                VirtualizationCacheLengthUnit.Item,
                VirtualizingPanel.GetCacheLengthUnit(table));
            var cacheLength = VirtualizingPanel.GetCacheLength(table);
            Assert.Equal(250, cacheLength.CacheBeforeViewport);
            Assert.Equal(250, cacheLength.CacheAfterViewport);
            Assert.False(ScrollViewer.GetIsDeferredScrollingEnabled(table));
        });
    }

    [Fact]
    public void FindsTheTableScrollViewerAfterItsTemplateIsApplied()
    {
        RunOnStaThread(() =>
        {
            var table = new DataGrid { Width = 320, Height = 180 };
            var window = new Window
            {
                Width = 400,
                Height = 260,
                Content = table,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };
            try
            {
                window.Show();
                table.UpdateLayout();

                var viewer = ScrollWheelRouter.FindScrollViewer(table);

                Assert.NotNull(viewer);
                Assert.Same(viewer, ScrollWheelRouter.FindScrollViewer(viewer!));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void LargeTableKeepsAnExpandedCacheWithoutRealizingEveryRow()
    {
        RunOnStaThread(() =>
        {
            const int rowCount = 10_000;
            var table = new DataGrid
            {
                AutoGenerateColumns = false,
                ItemsSource = Enumerable.Range(0, rowCount).ToArray(),
                RowHeight = 26
            };
            table.Columns.Add(new DataGridTextColumn
            {
                Header = "Value",
                Binding = new System.Windows.Data.Binding()
            });
            TableGridPerformance.Configure(table, cachedRowCount: 120);
            var window = new Window
            {
                Width = 400,
                Height = 260,
                Content = table,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };
            try
            {
                window.Show();
                table.ScrollIntoView(table.Items[rowCount / 2]);
                table.UpdateLayout();
                table.Dispatcher.Invoke(
                    static () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                table.UpdateLayout();

                var realizedRows = Enumerable.Range(0, rowCount)
                    .Count(index => table.ItemContainerGenerator.ContainerFromIndex(index) is not null);

                Assert.InRange(realizedRows, 100, 160);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ScrollHorizontallyMovesAHorizontalViewport()
    {
        RunOnStaThread(() =>
        {
            var viewer = new ScrollViewer
            {
                Width = 200,
                Height = 100,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new Border { Width = 1000, Height = 80 }
            };
            viewer.Measure(new Size(200, 100));
            viewer.Arrange(new Rect(0, 0, 200, 100));
            viewer.UpdateLayout();

            Assert.True(viewer.ScrollableWidth > 0);
            ScrollWheelRouter.ScrollHorizontally(viewer, 120);
            viewer.UpdateLayout();

            Assert.True(viewer.HorizontalOffset > 0);
        });
    }

    [Fact]
    public void MouseWheelMovesAHorizontalOnlyViewerInTheExpectedDirections()
    {
        RunOnStaThread(() =>
        {
            ScrollWheelRouter.Register();
            var content = new Border { Width = 1000, Height = 80 };
            var viewer = new ScrollViewer
            {
                Width = 200,
                Height = 100,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = content
            };
            viewer.Measure(new Size(200, 100));
            viewer.Arrange(new Rect(0, 0, 200, 100));
            viewer.UpdateLayout();
            var wheelUp = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
            {
                RoutedEvent = UIElement.PreviewMouseWheelEvent
            };

            content.RaiseEvent(wheelUp);
            viewer.UpdateLayout();

            Assert.True(wheelUp.Handled);
            Assert.True(viewer.HorizontalOffset > 0);

            var offsetAfterWheelUp = viewer.HorizontalOffset;
            var wheelDown = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            {
                RoutedEvent = UIElement.PreviewMouseWheelEvent
            };

            content.RaiseEvent(wheelDown);
            viewer.UpdateLayout();

            Assert.True(wheelDown.Handled);
            Assert.True(viewer.HorizontalOffset < offsetAfterWheelUp);
        });
    }

    [Fact]
    public void CompositeViewerWheelOverItsHorizontalBarMovesColumns()
    {
        RunOnStaThread(() =>
        {
            var viewer = new ScrollViewer
            {
                Width = 320,
                Height = 180,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Border { Width = 1200, Height = 800 }
            };
            var horizontalBar = new ScrollBar
            {
                Orientation = Orientation.Horizontal,
                Height = 16,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            var tableSurface = new Grid();
            tableSurface.Children.Add(viewer);
            tableSurface.Children.Add(horizontalBar);
            tableSurface.Measure(new Size(320, 180));
            tableSurface.Arrange(new Rect(0, 0, 320, 180));
            tableSurface.UpdateLayout();

            var routed = ScrollWheelRouter.TryRouteHorizontalWheel(
                tableSurface,
                horizontalBar,
                120,
                ModifierKeys.None);
            tableSurface.UpdateLayout();

            Assert.True(routed);
            Assert.True(viewer.HorizontalOffset > 0);
        });
    }

    [Fact]
    public void TableShiftWheelMovesColumnsWithoutTakingNormalVerticalWheel()
    {
        RunOnStaThread(() =>
        {
            var content = new Border { Width = 1000, Height = 1000 };
            var viewer = new ScrollViewer
            {
                Width = 200,
                Height = 100,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content
            };
            viewer.Measure(new Size(200, 100));
            viewer.Arrange(new Rect(0, 0, 200, 100));
            viewer.UpdateLayout();

            Assert.False(ScrollWheelRouter.TryRouteHorizontalWheel(
                viewer,
                content,
                120,
                ModifierKeys.None));
            Assert.True(ScrollWheelRouter.TryRouteHorizontalWheel(
                viewer,
                content,
                120,
                ModifierKeys.Shift));
            viewer.UpdateLayout();

            Assert.True(viewer.HorizontalOffset > 0);
        });
    }

    [Fact]
    public void HorizontalScrollZoneMatchesTheBottomTrackButNotTheVerticalCorner()
    {
        RunOnStaThread(() =>
        {
            var surface = new Border { Width = 320, Height = 180 };
            surface.Measure(new Size(320, 180));
            surface.Arrange(new Rect(0, 0, 320, 180));

            Assert.True(ScrollWheelRouter.IsOverHorizontalScrollZone(
                surface,
                new Point(120, 180 - SystemParameters.HorizontalScrollBarHeight / 2)));
            Assert.False(ScrollWheelRouter.IsOverHorizontalScrollZone(
                surface,
                new Point(120, 80)));
            Assert.False(ScrollWheelRouter.IsOverHorizontalScrollZone(
                surface,
                new Point(320 - SystemParameters.VerticalScrollBarWidth / 2, 175)));
        });
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
