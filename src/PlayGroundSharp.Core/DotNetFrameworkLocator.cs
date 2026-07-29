using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PlayGroundSharp.Core;

/// <summary>A target framework and its installed reference assembly directory.</summary>
public sealed record DotNetFrameworkInfo(
    string TargetFramework,
    string DisplayName,
    Version Version,
    string? ReferenceDirectory)
{
    private IReadOnlyList<string>? referencePaths;

    public IReadOnlyList<string> GetReferencePaths() =>
        referencePaths ??= ReferenceDirectory is { } directory && Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.dll")
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

    public override string ToString() => DisplayName;
}

/// <summary>Finds .NET targeting packs that can be used by the interactive compiler.</summary>
public static partial class DotNetFrameworkLocator
{
    public static IReadOnlyList<DotNetFrameworkInfo> Discover(
        string? dotNetRoot = null,
        int? maximumMajorVersion = null)
    {
        var root = dotNetRoot ?? FindDotNetRoot();
        var packRoot = root is null
            ? null
            : Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref");
        if (packRoot is null || !Directory.Exists(packRoot)) return [];

        var maximumMajor = maximumMajorVersion ?? Environment.Version.Major;
        return Directory.EnumerateDirectories(packRoot)
            .Select(static path => new
            {
                Path = path,
                Version = Version.TryParse(Path.GetFileName(path), out var version) ? version : null
            })
            .Where(item => item.Version is { Major: > 0 } version && version.Major <= maximumMajor)
            .Select(item => new
            {
                item.Path,
                Version = item.Version!,
                TargetFramework = $"net{item.Version!.Major}.0",
                ReferenceDirectory = Path.Combine(item.Path, "ref", $"net{item.Version.Major}.0")
            })
            .Where(static item => Directory.Exists(item.ReferenceDirectory) &&
                                  Directory.EnumerateFiles(item.ReferenceDirectory, "*.dll").Any())
            .GroupBy(static item => item.TargetFramework, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(static item => item.Version).First())
            .OrderByDescending(static item => item.Version)
            .Select(static item => new DotNetFrameworkInfo(
                item.TargetFramework,
                $".NET {item.Version.Major}",
                item.Version,
                Path.GetFullPath(item.ReferenceDirectory)))
            .ToArray();
    }

    public static DotNetFrameworkInfo CurrentRuntimeFallback() => new(
        $"net{Environment.Version.Major}.0",
        $".NET {Environment.Version.Major}",
        Environment.Version,
        null);

    public static bool IsValidTargetFramework(string? value) =>
        value is not null && TargetFrameworkPattern().IsMatch(value);

    private static string? FindDotNetRoot()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            return Path.GetFullPath(configured);

        var runtimeDirectory = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        return runtimeDirectory.Parent?.Parent?.Parent?.FullName;
    }

    [GeneratedRegex("^net[1-9][0-9]*\\.0$", RegexOptions.CultureInvariant)]
    private static partial Regex TargetFrameworkPattern();
}
