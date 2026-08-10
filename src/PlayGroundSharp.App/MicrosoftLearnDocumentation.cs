using System.Text.RegularExpressions;

namespace PlayGroundSharp.App;

internal static partial class MicrosoftLearnDocumentation
{
    public static Uri CreateUri(
        string documentationPath,
        AppLanguageMode languageMode,
        string targetFramework)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentationPath);
        var locale = languageMode == AppLanguageMode.Japanese ? "ja-jp" : "en-us";
        var match = TargetFrameworkPattern().Match(targetFramework);
        var view = match.Success ? $"net-{match.Groups["version"].Value}" : "net";
        return new UriBuilder("https", "learn.microsoft.com")
        {
            Path = $"{locale}/dotnet/api/{documentationPath}",
            Query = $"view={Uri.EscapeDataString(view)}"
        }.Uri;
    }

    [GeneratedRegex("^net(?<version>\\d+(?:\\.\\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex TargetFrameworkPattern();
}
