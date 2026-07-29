using System.Diagnostics;
using PlayGroundSharp.Core;
using PlayGroundSharp.TestFixture;
using PlayGroundSharp.Worker;

namespace PlayGroundSharp.Worker.Tests;

public sealed class PackageRestoreServiceTests
{
    [Fact]
    public void RejectsInvalidTargetFramework()
    {
        Assert.Throws<ArgumentException>(() =>
            new PackageRestoreService(targetFramework: "../net10.0"));
    }

    [Fact]
    public async Task RestoresPackageAndTransitiveDependencyFromLocalFeed()
    {
        var repository = FindRepositoryRoot();
        var temporary = Path.Combine(Path.GetTempPath(), "PlayGroundSharp.Tests", Guid.NewGuid().ToString("N"));
        var feed = Path.Combine(temporary, "feed");
        var cache = Path.Combine(temporary, "cache");
        Directory.CreateDirectory(feed);
        try
        {
            await PackAsync(Path.Combine(repository, "tests", "Fixtures", "PlayGroundSharp.TestDependency", "PlayGroundSharp.TestDependency.csproj"), feed);
            await PackAsync(Path.Combine(repository, "tests", "Fixtures", "PlayGroundSharp.TestFixture", "PlayGroundSharp.TestFixture.csproj"), feed);
            var result = await new PackageRestoreService(cache).RestoreAsync("PlayGroundSharp.TestFixture", "1.0.0", feed);

            Assert.Equal("1.0.0", result.Version);
            Assert.Contains(result.AssemblyPaths, static path => path.EndsWith("PlayGroundSharp.TestFixture.dll", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.AssemblyPaths, static path => path.EndsWith("PlayGroundSharp.TestDependency.dll", StringComparison.OrdinalIgnoreCase));
            var session = new ScriptSession();
            foreach (var path in result.AssemblyPaths) session.AddReference(path);
            Assert.Equal("hello from fixture", (await session.ExecuteAsync(1, "PlayGroundSharp.TestFixture.Greeter.Message")).Snapshot?.Display);

            var olderFramework = DotNetFrameworkLocator.Discover()
                .FirstOrDefault(static candidate => candidate.Version.Major == 9);
            if (olderFramework is not null)
            {
                var olderResult = await new PackageRestoreService(
                        Path.Combine(temporary, "cache-net9"),
                        olderFramework.TargetFramework)
                    .RestoreAsync("PlayGroundSharp.TestFixture", "1.0.0", feed);
                var olderSession = new ScriptSession(
                    olderFramework.GetReferencePaths(),
                    olderFramework.TargetFramework);
                foreach (var path in olderResult.AssemblyPaths) olderSession.AddReference(path);
                Assert.Equal(
                    "hello from fixture",
                    (await olderSession.ExecuteAsync(
                        1,
                        "PlayGroundSharp.TestFixture.Greeter.Message")).Snapshot?.Display);
            }
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    private static async Task PackAsync(string project, string output)
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", $"pack \"{project}\" -c Debug --no-restore -o \"{output}\" --nologo")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("dotnet pack failed to start.");
        var outputText = await process.StandardOutput.ReadToEndAsync();
        var errorText = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, outputText + errorText);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PlayGroundSharp.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
