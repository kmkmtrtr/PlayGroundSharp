using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PlayGroundSharp.App.Tests;

public sealed class ScrollWheelRouterTests
{
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
    public void MouseWheelMovesAHorizontalOnlyViewer()
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
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            {
                RoutedEvent = UIElement.PreviewMouseWheelEvent
            };

            content.RaiseEvent(wheel);
            viewer.UpdateLayout();

            Assert.True(wheel.Handled);
            Assert.True(viewer.HorizontalOffset > 0);
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
