using System.Collections;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App.Tests;

public sealed class TargetFrameworkSelectionTests
{
    [Fact]
    public void ResolvesNewItemFromSelectionChangedEvent()
    {
        var previous = Framework("net10.0", 10);
        var selected = Framework("net9.0", 9);
        var args = new SelectionChangedEventArgs(
            Selector.SelectionChangedEvent,
            new ArrayList { previous },
            new ArrayList { selected });

        Assert.Same(selected, TargetFrameworkSelection.Resolve(args));
    }

    private static DotNetFrameworkInfo Framework(string targetFramework, int major) =>
        new(targetFramework, $".NET {major}", new Version(major, 0), null);
}
