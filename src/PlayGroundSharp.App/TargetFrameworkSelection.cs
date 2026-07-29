using System.Windows.Controls;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

internal static class TargetFrameworkSelection
{
    public static DotNetFrameworkInfo? Resolve(SelectionChangedEventArgs args) =>
        args.AddedItems.OfType<DotNetFrameworkInfo>().LastOrDefault();
}
