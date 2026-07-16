using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PlayGroundSharp.Worker;

public sealed record PackageRestoreResult(string PackageId, string Version, IReadOnlyList<string> AssemblyPaths);

/// <summary>Restores a package through the .NET SDK and extracts compatible runtime assets.</summary>
public sealed partial class PackageRestoreService
{
    private readonly string packageCache;

    public PackageRestoreService(string? packageCache = null)
    {
        this.packageCache = packageCache ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlayGroundSharp", "packages");
    }

    public async Task<PackageRestoreResult> RestoreAsync(
        string packageId,
        string? version = null,
        string? source = null,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!PackageIdPattern().IsMatch(packageId))
            throw new ArgumentException("Package ID contains invalid characters.", nameof(packageId));
        if (version is not null && !VersionPattern().IsMatch(version))
            throw new ArgumentException("Package version is invalid.", nameof(version));
        if (source is not null && !Directory.Exists(source) && !Uri.TryCreate(source, UriKind.Absolute, out _))
            throw new ArgumentException("Package source must be an existing directory or absolute URI.", nameof(source));

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "PlayGroundSharp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        Directory.CreateDirectory(packageCache);
        try
        {
            var projectPath = Path.Combine(temporaryDirectory, "Restore.csproj");
            var selectedVersion = version ?? "*";
            await File.WriteAllTextAsync(projectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><PackageReference Include="{{packageId}}" Version="{{selectedVersion}}" /></ItemGroup>
                </Project>
                """, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            progress?.Invoke($"Restoring {packageId} {selectedVersion}…");
            var arguments = new StringBuilder($"restore \"{projectPath}\" --packages \"{packageCache}\" --nologo");
            if (source is not null)
            {
                var configPath = Path.Combine(temporaryDirectory, "NuGet.Config");
                await File.WriteAllTextAsync(configPath, $$"""
                    <?xml version="1.0" encoding="utf-8"?>
                    <configuration><packageSources><clear /><add key="requested" value="{{source}}" /></packageSources></configuration>
                    """, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                arguments.Append($" --configfile \"{configPath}\"");
            }
            var startInfo = new ProcessStartInfo("dotnet", arguments.ToString())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = temporaryDirectory
            };
            if (source is not null)
                startInfo.Environment["APPDATA"] = temporaryDirectory;
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("dotnet restore could not be started.");
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            var output = await standardOutput.ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Package restore failed ({process.ExitCode}). {error}{output}".Trim());

            var assetsPath = Path.Combine(temporaryDirectory, "obj", "project.assets.json");
            await using var assetsStream = File.OpenRead(assetsPath);
            using var assets = await JsonDocument.ParseAsync(assetsStream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = assets.RootElement;
            var target = root.GetProperty("targets").EnumerateObject()
                .FirstOrDefault(static item => item.Name.StartsWith("net10.0", StringComparison.OrdinalIgnoreCase));
            if (target.Value.ValueKind == JsonValueKind.Undefined)
                throw new InvalidOperationException("Restore produced no net10.0 target.");
            var packageFolders = root.GetProperty("packageFolders").EnumerateObject().Select(static item => item.Name).ToArray();
            var libraries = root.GetProperty("libraries");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? resolvedVersion = null;
            foreach (var library in target.Value.EnumerateObject())
            {
                var separator = library.Name.LastIndexOf('/');
                if (separator < 1) continue;
                var id = library.Name[..separator];
                var libraryVersion = library.Name[(separator + 1)..];
                if (id.Equals(packageId, StringComparison.OrdinalIgnoreCase)) resolvedVersion = libraryVersion;
                if (!libraries.TryGetProperty(library.Name, out var libraryInfo) ||
                    !libraryInfo.TryGetProperty("path", out var relativePathElement)) continue;
                var assetGroup = library.Value.TryGetProperty("runtime", out var runtime) ? runtime :
                    library.Value.TryGetProperty("compile", out var compile) ? compile : default;
                if (assetGroup.ValueKind != JsonValueKind.Object) continue;
                foreach (var asset in assetGroup.EnumerateObject().Where(static item => item.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var folder in packageFolders)
                    {
                        var path = Path.GetFullPath(Path.Combine(folder, relativePathElement.GetString()!, asset.Name.Replace('/', Path.DirectorySeparatorChar)));
                        if (File.Exists(path)) { paths.Add(path); break; }
                    }
                }
            }
            if (resolvedVersion is null) throw new InvalidOperationException($"Package '{packageId}' was not found in restore assets.");
            progress?.Invoke($"Restored {packageId} {resolvedVersion} ({paths.Count} assemblies)." );
            return new(packageId, resolvedVersion, paths.ToArray());
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A successful restore must not be reported as failed because an antivirus still holds a temp file.
            }
        }
    }

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,98}[A-Za-z0-9])?$")]
    private static partial Regex PackageIdPattern();
    [GeneratedRegex("^[0-9A-Za-z][0-9A-Za-z.+-]{0,99}$")]
    private static partial Regex VersionPattern();
}
