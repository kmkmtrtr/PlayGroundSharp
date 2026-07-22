using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace PlayGroundSharp.Core;

/// <summary>Resolves framework assemblies required by dynamically added package assemblies.</summary>
public static class PlatformReferenceResolver
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> RuntimeAssemblies = new(
        CreateRuntimeAssemblyMap,
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> AssemblyReferenceCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> ResolveRuntimeDependencies(IEnumerable<string> assemblyPaths)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(assemblyPaths.Where(File.Exists).Select(Path.GetFullPath));
        while (pending.TryDequeue(out var path))
        {
            if (!visitedPaths.Add(path)) continue;
            foreach (var assemblyName in AssemblyReferenceCache.GetOrAdd(path, ReadAssemblyReferences))
            {
                if (!RuntimeAssemblies.Value.TryGetValue(assemblyName, out var dependencyPath) ||
                    !resolved.TryAdd(assemblyName, dependencyPath)) continue;
                pending.Enqueue(dependencyPath);
            }
        }
        return resolved.Values.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyDictionary<string, string> CreateRuntimeAssemblyMap()
    {
        var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        return Directory.EnumerateFiles(runtimeDirectory, "*.dll")
            .ToDictionary(
                static path => Path.GetFileNameWithoutExtension(path),
                Path.GetFullPath,
                StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ReadAssemblyReferences(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata) return [];
            var metadata = peReader.GetMetadataReader();
            return metadata.AssemblyReferences
                .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return [];
        }
    }
}
