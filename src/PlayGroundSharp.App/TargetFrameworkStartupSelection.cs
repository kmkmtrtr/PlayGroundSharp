using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

internal sealed record TargetFrameworkStartupSelection(
    DotNetFrameworkInfo SelectedFramework,
    string? UnavailableSavedTargetFramework);

internal static class TargetFrameworkStartupSelector
{
    public static TargetFrameworkStartupSelection Select(
        string? savedTargetFramework,
        IReadOnlyList<DotNetFrameworkInfo> availableFrameworks,
        int currentRuntimeMajor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(currentRuntimeMajor);
        if (availableFrameworks.Count == 0)
            throw new ArgumentException("At least one target framework is required.", nameof(availableFrameworks));

        var saved = availableFrameworks.FirstOrDefault(framework =>
            framework.TargetFramework.Equals(savedTargetFramework, StringComparison.OrdinalIgnoreCase));
        if (saved is not null) return new(saved, null);

        var currentTargetFramework = $"net{currentRuntimeMajor}.0";
        var fallback = availableFrameworks.FirstOrDefault(framework =>
                           framework.TargetFramework.Equals(currentTargetFramework, StringComparison.OrdinalIgnoreCase))
                       ?? availableFrameworks[0];
        return new(
            fallback,
            string.IsNullOrWhiteSpace(savedTargetFramework) ? null : savedTargetFramework);
    }
}
