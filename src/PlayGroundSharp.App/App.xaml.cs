using System.Windows;
using System.Windows.Media;

namespace PlayGroundSharp.App;

public partial class App : Application
{
    public static void ApplyLanguage(AppLanguageMode mode)
    {
        if (Current is null) return;
        foreach (var (key, value) in AppLocalization.Resources(mode)) Current.Resources[key] = value;
    }

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
                ["ErrorBrush"] = "#F87171", ["WarningBrush"] = "#FBBF24",
                ["HoverBrush"] = "#2D3440", ["SelectionBrush"] = "#1E3A5F",
                ["AccentHoverBrush"] = "#3B82F6", ["OverlayBrush"] = "#2B2B2F",
                ["ExplorerNamespaceBrush"] = "#C4B5FD", ["ExplorerClassBrush"] = "#93C5FD",
                ["ExplorerRecordBrush"] = "#67E8F9", ["ExplorerInterfaceBrush"] = "#6EE7B7",
                ["ExplorerStructBrush"] = "#FCD34D", ["ExplorerEnumBrush"] = "#D8B4FE",
                ["ExplorerDelegateBrush"] = "#F9A8D4", ["ExplorerMethodBrush"] = "#5EEAD4"
            }
            : new Dictionary<string, string>
            {
                ["BackgroundBrush"] = "#FFFFFF", ["PanelBrush"] = "#F8F9FA",
                ["BorderBrush"] = "#DADCE0", ["ForegroundBrush"] = "#202124",
                ["MutedBrush"] = "#5F6368", ["AccentBrush"] = "#1967D2",
                ["InputBrush"] = "#FFFFFF", ["DrawerBrush"] = "#F8F9FA",
                ["ErrorBrush"] = "#B3261E", ["WarningBrush"] = "#8B6914",
                ["HoverBrush"] = "#EDF3FF", ["SelectionBrush"] = "#DBEAFE",
                ["AccentHoverBrush"] = "#1D4ED8", ["OverlayBrush"] = "#FFFFFF",
                ["ExplorerNamespaceBrush"] = "#7C3AED", ["ExplorerClassBrush"] = "#2563EB",
                ["ExplorerRecordBrush"] = "#0891B2", ["ExplorerInterfaceBrush"] = "#059669",
                ["ExplorerStructBrush"] = "#D97706", ["ExplorerEnumBrush"] = "#9333EA",
                ["ExplorerDelegateBrush"] = "#DB2777", ["ExplorerMethodBrush"] = "#0F766E"
            };
        foreach (var (key, value) in colors)
            Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    }
}
