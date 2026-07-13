using System.IO;
using System.Text.Json;

namespace PlayGroundSharp.App;

public enum ExecutionKeyMode { Enter, ControlEnter }
public enum AppThemeMode { Light, Dark }
public enum AppLanguageMode { Japanese, English }

internal sealed record AppSettings(
    ExecutionKeyMode ExecutionKeyMode = ExecutionKeyMode.Enter,
    AppThemeMode ThemeMode = AppThemeMode.Light,
    AppLanguageMode LanguageMode = AppLanguageMode.Japanese);

internal static class SettingsStore
{
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
                ? settings
                : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Settings persistence must never make the interactive console unusable.
        }
    }
}
