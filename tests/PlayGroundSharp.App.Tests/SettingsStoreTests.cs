namespace PlayGroundSharp.App.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void RepairsInvalidInspectorBoundsBeforeTheyAreReused()
    {
        var settings = new AppSettings(
            InspectorWidth: double.NegativeInfinity,
            InspectorHeight: double.NaN,
            InspectorTreeHeight: double.PositiveInfinity);

        var normalized = SettingsStore.Normalize(settings);

        Assert.Equal(760, normalized.InspectorWidth);
        Assert.Equal(560, normalized.InspectorHeight);
        Assert.Equal(280, normalized.InspectorTreeHeight);
    }

    [Fact]
    public void KeepsValidInspectorBounds()
    {
        var settings = new AppSettings(
            InspectorWidth: 1_200,
            InspectorHeight: 800,
            InspectorTreeHeight: 420);

        var normalized = SettingsStore.Normalize(settings);

        Assert.Equal(1_200, normalized.InspectorWidth);
        Assert.Equal(800, normalized.InspectorHeight);
        Assert.Equal(420, normalized.InspectorTreeHeight);
    }
}
