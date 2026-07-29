using PlayGroundSharp.Core;

namespace PlayGroundSharp.Core.Tests;

public sealed class DotNetFrameworkLocatorTests
{
    [Fact]
    public void DiscoversLatestInstalledPackForEachSupportedMajorVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "PlayGroundSharp.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            CreateReference(root, "9.0.1", "net9.0");
            CreateReference(root, "9.0.8", "net9.0");
            CreateReference(root, "10.0.2", "net10.0");
            CreateReference(root, "11.0.0", "net11.0");

            var frameworks = DotNetFrameworkLocator.Discover(root, maximumMajorVersion: 10);

            Assert.Collection(
                frameworks,
                framework =>
                {
                    Assert.Equal("net10.0", framework.TargetFramework);
                    Assert.Equal(new Version(10, 0, 2), framework.Version);
                },
                framework =>
                {
                    Assert.Equal("net9.0", framework.TargetFramework);
                    Assert.Equal(new Version(9, 0, 8), framework.Version);
                });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("net9.0", true)]
    [InlineData("net10.0", true)]
    [InlineData("net10.0-windows", false)]
    [InlineData("../net10.0", false)]
    [InlineData("", false)]
    public void ValidatesTargetFramework(string value, bool expected)
    {
        Assert.Equal(expected, DotNetFrameworkLocator.IsValidTargetFramework(value));
    }

    private static void CreateReference(string root, string packVersion, string targetFramework)
    {
        var directory = Path.Combine(
            root,
            "packs",
            "Microsoft.NETCore.App.Ref",
            packVersion,
            "ref",
            targetFramework);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "System.Runtime.dll"), [0]);
    }
}
