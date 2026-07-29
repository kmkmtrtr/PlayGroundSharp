using PlayGroundSharp.Core;

namespace PlayGroundSharp.Worker;

public sealed record WorkerConfiguration(
    string PipeName,
    string TargetFramework,
    string? FrameworkReferenceDirectory);

public static class WorkerCommandLine
{
    public static bool TryParse(IReadOnlyList<string> args, out WorkerConfiguration? configuration)
    {
        configuration = null;
        string? pipeName = null;
        string? targetFramework = null;
        string? referenceDirectory = null;
        for (var index = 0; index < args.Count; index += 2)
        {
            if (index + 1 >= args.Count) return false;
            var value = args[index + 1];
            if (string.IsNullOrWhiteSpace(value)) return false;
            switch (args[index])
            {
                case "--pipe" when pipeName is null:
                    pipeName = value;
                    break;
                case "--target-framework" when targetFramework is null:
                    targetFramework = value;
                    break;
                case "--framework-reference-directory" when referenceDirectory is null:
                    referenceDirectory = value;
                    break;
                default:
                    return false;
            }
        }

        targetFramework ??= $"net{Environment.Version.Major}.0";
        if (pipeName is null || !DotNetFrameworkLocator.IsValidTargetFramework(targetFramework))
            return false;
        if (referenceDirectory is not null)
        {
            if (!Directory.Exists(referenceDirectory) ||
                !Directory.EnumerateFiles(referenceDirectory, "*.dll").Any())
                return false;
            referenceDirectory = Path.GetFullPath(referenceDirectory);
        }

        configuration = new(pipeName, targetFramework, referenceDirectory);
        return true;
    }
}
