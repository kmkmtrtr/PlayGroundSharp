using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.Worker;

internal sealed record TargetFrameworkGlobals(object Instance, Type Type, string AssemblyPath);

/// <summary>
/// Creates a globals type compiled against the selected framework. Keeping host objects behind
/// dynamic properties prevents the net10.0 Worker assembly from raising the selected API surface.
/// </summary>
internal static class TargetFrameworkGlobalsFactory
{
    private const string TypeName = "PlayGroundSharp.Generated.SessionGlobals";
    private static readonly object CleanupGate = new();
    private static readonly List<string> GeneratedPaths = [];

    static TargetFrameworkGlobalsFactory()
    {
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => CleanupGeneratedAssemblies();
    }

    public static TargetFrameworkGlobals Create(
        string targetFramework,
        IReadOnlyList<string> frameworkReferencePaths)
    {
        if (!DotNetFrameworkLocator.IsValidTargetFramework(targetFramework))
            throw new ArgumentException("Target framework is invalid.", nameof(targetFramework));
        var directory = GetWritableDirectory();
        var assemblyPath = Path.Combine(
            directory,
            $"PlayGroundSharp.ScriptGlobals.{targetFramework}.{Guid.NewGuid():N}.dll");
        CompileAssembly(assemblyPath, frameworkReferencePaths);
        lock (CleanupGate) GeneratedPaths.Add(assemblyPath);
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        var type = assembly.GetType(TypeName, throwOnError: true)!;
        return new(Activator.CreateInstance(type)!, type, assemblyPath);
    }

    private static string GetWritableDirectory()
    {
        var preferred = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlayGroundSharp",
            "generated");
        try
        {
            Directory.CreateDirectory(preferred);
            var probe = Path.Combine(preferred, $".write-probe-{Guid.NewGuid():N}");
            using (File.Create(probe))
            {
            }
            File.Delete(probe);
            return preferred;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var fallback = Path.Combine(Path.GetTempPath(), "PlayGroundSharp", "generated");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private static void CompileAssembly(
        string assemblyPath,
        IReadOnlyList<string> frameworkReferencePaths)
    {
        var source = """
            namespace PlayGroundSharp.Generated
            {
                public sealed class SessionGlobals
                {
                    public dynamic Last { get; set; }
                    public dynamic Out { get; set; }
                    public dynamic Data { get; set; }
                    public System.Threading.CancellationToken ExecutionCancellation { get; set; }

                    public T RetainResultAs<T>(int index)
                    {
                        T value = (T)Out[index];
                        Out.MarkNamed(index);
                        return value;
                    }

                    public dynamic RetainResultAsDynamic(int index)
                    {
                        dynamic value = Out[index];
                        Out.MarkNamed(index);
                        return value;
                    }
                    public void ReleaseResult(int index) => Out.Release(index);
                }
            }
            """;
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = frameworkReferencePaths
            .Where(File.Exists)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(assemblyPath),
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        try
        {
            using (var stream = new FileStream(assemblyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var result = compilation.Emit(stream);
                if (!result.Success)
                    throw new InvalidOperationException(
                        "Could not create target-framework globals: " +
                        string.Join(Environment.NewLine, result.Diagnostics
                            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
            }
        }
        catch
        {
            TryDelete(assemblyPath);
            throw;
        }
    }

    private static void CleanupGeneratedAssemblies()
    {
        lock (CleanupGate)
        {
            foreach (var path in GeneratedPaths) TryDelete(path);
            GeneratedPaths.Clear();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The operating system will reclaim temporary generated files later.
        }
    }
}
