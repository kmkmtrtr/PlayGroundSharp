using System.Windows;
using System.Windows.Media;

namespace PlayGroundSharp.App;

public partial class App : Application
{
    public static void ApplyTheme(AppThemeMode mode)
    {
        if (Current is null) return;
        var colors = mode == AppThemeMode.Dark
            ? new Dictionary<string, string>
            {
                ["BackgroundBrush"] = "#1E1E1E", ["PanelBrush"] = "#252526",
                ["BorderBrush"] = "#3F3F46", ["ForegroundBrush"] = "#F3F4F6",
                ["MutedBrush"] = "#A1A1AA", ["AccentBrush"] = "#60A5FA",
                ["InputBrush"] = "#18181B", ["DrawerBrush"] = "#252526",
                ["ErrorBrush"] = "#F87171", ["WarningBrush"] = "#FBBF24"
            }
            : new Dictionary<string, string>
            {
                ["BackgroundBrush"] = "#FFFFFF", ["PanelBrush"] = "#F8F9FA",
                ["BorderBrush"] = "#DADCE0", ["ForegroundBrush"] = "#202124",
                ["MutedBrush"] = "#5F6368", ["AccentBrush"] = "#1967D2",
                ["InputBrush"] = "#FFFFFF", ["DrawerBrush"] = "#F8F9FA",
                ["ErrorBrush"] = "#B3261E", ["WarningBrush"] = "#8B6914"
            };
        foreach (var (key, value) in colors)
            Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    }
}
