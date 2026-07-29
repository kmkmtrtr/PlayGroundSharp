using PlayGroundSharp.Core;

namespace PlayGroundSharp.App.Tests;

public sealed class TargetFrameworkStartupSelectorTests
{
    private static readonly DotNetFrameworkInfo Net10 =
        new("net10.0", ".NET 10", new Version(10, 0), null);
    private static readonly DotNetFrameworkInfo Net9 =
        new("net9.0", ".NET 9", new Version(9, 0), @"C:\dotnet\packs\net9.0");

    [Fact]
    public void KeepsAvailableSavedTargetFramework()
    {
        var result = TargetFrameworkStartupSelector.Select("NET9.0", [Net10, Net9], 10);

        Assert.Same(Net9, result.SelectedFramework);
        Assert.Null(result.UnavailableSavedTargetFramework);
    }

    [Fact]
    public void FallsBackToCurrentRuntimeWhenSavedTargetFrameworkIsUnavailable()
    {
        var result = TargetFrameworkStartupSelector.Select("net8.0", [Net10, Net9], 10);

        Assert.Same(Net10, result.SelectedFramework);
        Assert.Equal("net8.0", result.UnavailableSavedTargetFramework);
    }

    [Fact]
    public void UsesFirstAvailableFrameworkWhenCurrentRuntimeIsNotListed()
    {
        var result = TargetFrameworkStartupSelector.Select("net8.0", [Net9], 10);

        Assert.Same(Net9, result.SelectedFramework);
        Assert.Equal("net8.0", result.UnavailableSavedTargetFramework);
    }

    [Fact]
    public void DoesNotReportFallbackWhenNoTargetWasSaved()
    {
        var result = TargetFrameworkStartupSelector.Select(null, [Net10, Net9], 10);

        Assert.Same(Net10, result.SelectedFramework);
        Assert.Null(result.UnavailableSavedTargetFramework);
    }
}
