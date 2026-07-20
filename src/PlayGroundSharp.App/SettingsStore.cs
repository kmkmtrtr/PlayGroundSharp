using System.IO;
using System.Text.Json;

namespace PlayGroundSharp.App;

public enum ExecutionKeyMode { Enter, ControlEnter }
public enum AppThemeMode { Light, Dark }
public enum AppLanguageMode { Japanese, English }

internal sealed record AppSettings(
    ExecutionKeyMode ExecutionKeyMode = ExecutionKeyMode.Enter,
    AppThemeMode ThemeMode = AppThemeMode.Light,
    AppLanguageMode LanguageMode = AppLanguageMode.Japanese,
    double WindowWidth = 1200,
    double WindowHeight = 800,
    double? WindowLeft = null,
    double? WindowTop = null,
    bool IsWindowMaximized = false,
    bool IsTypeExplorerOpen = true,
    bool IsReferenceDrawerOpen = false,
    double TypeExplorerWidth = 286,
    double ReferenceDrawerWidth = 470,
    int WorkspaceTabIndex = 0,
    double CompletionListWidth = 390,
    double InspectorWidth = 760,
    double InspectorHeight = 560,
    double InspectorTreeHeight = 280);

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PlayGroundSharp",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new()
                : new();
            return Enum.IsDefined(settings.ExecutionKeyMode) && Enum.IsDefined(settings.ThemeMode) &&
                Enum.IsDefined(settings.LanguageMode)
                ? Normalize(settings)
                : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    private static AppSettings Normalize(AppSettings settings) => settings with
    {
        WindowWidth = Normalize(settings.WindowWidth, 820, 10_000, 1200),
        WindowHeight = Normalize(settings.WindowHeight, 560, 10_000, 800),
        WindowLeft = NormalizeCoordinate(settings.WindowLeft),
        WindowTop = NormalizeCoordinate(settings.WindowTop),
        TypeExplorerWidth = Normalize(settings.TypeExplorerWidth, 220, 520, 286),
        ReferenceDrawerWidth = Normalize(settings.ReferenceDrawerWidth, 320, 720, 470),
        WorkspaceTabIndex = Math.Clamp(settings.WorkspaceTabIndex, 0, 4),
        CompletionListWidth = Normalize(settings.CompletionListWidth, 250, 525, 390),
        InspectorWidth = Normalize(settings.InspectorWidth, 480, 4_000, 760),
        InspectorHeight = Normalize(settings.InspectorHeight, 340, 3_000, 560),
        InspectorTreeHeight = Normalize(settings.InspectorTreeHeight, 120, 2_000, 280)
    };

    private static double Normalize(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) && value >= minimum && value <= maximum ? value : fallback;

    private static double? NormalizeCoordinate(double? value) =>
        value is { } coordinate && double.IsFinite(coordinate) ? coordinate : null;

    public static void Save(AppSettings settings)
    {
        string? temporaryPath = null;
        try
        {
            settings = Normalize(settings);
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".settings.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, Options));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Settings persistence must never make the interactive console unusable.
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // A stale temporary settings file is harmless and can be ignored.
                }
            }
        }
    }
}
