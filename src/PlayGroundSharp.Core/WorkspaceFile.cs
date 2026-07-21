using System.Text.Encodings.Web;
using System.Text.Json;

namespace PlayGroundSharp.Core;

/// <summary>Identifies an exact package restored by a saved workspace.</summary>
public sealed record WorkspacePackage(string Id, string Version);

/// <summary>Portable state that can reconstruct a PlayGroundSharp session by replaying submissions.</summary>
public sealed record WorkspaceDocument(
    int Version,
    DateTime SavedAtUtc,
    IReadOnlyList<string> Submissions,
    IReadOnlyList<string> Imports,
    IReadOnlyList<string> References,
    IReadOnlyList<WorkspacePackage> Packages,
    string InputText)
{
    public const int CurrentVersion = 1;
}

/// <summary>Reads and atomically writes bounded PlayGroundSharp workspace files.</summary>
public static class WorkspaceFile
{
    public const long MaximumFileBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task SaveAsync(
        string path,
        WorkspaceDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Validate(document);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Workspace path has no directory.", nameof(path));
        var storedDocument = document with
        {
            References = document.References
                .Select(reference => GetStoredReferencePath(directory, reference))
                .ToArray()
        };
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 65_536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, storedDocument, Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (stream.Length > MaximumFileBytes)
                    throw new InvalidDataException("Workspace file exceeds 16 MiB.");
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Preserve the original save outcome if a scanner still holds an abandoned temp file.
            }
        }
    }

    public static async Task<WorkspaceDocument> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists) throw new FileNotFoundException("Workspace file was not found.", file.FullName);
        if (file.Length > MaximumFileBytes) throw new InvalidDataException("Workspace file exceeds 16 MiB.");
        await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await JsonSerializer.DeserializeAsync<WorkspaceDocument>(stream, Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Workspace file is empty or invalid.");
        Validate(document);
        var directory = file.DirectoryName
            ?? throw new InvalidDataException("Workspace path has no directory.");
        return document with
        {
            References = document.References
                .Select(reference => Path.IsPathRooted(reference)
                    ? Path.GetFullPath(reference)
                    : Path.GetFullPath(reference, directory))
                .ToArray()
        };
    }

    private static string GetStoredReferencePath(string workspaceDirectory, string reference)
    {
        var fullPath = Path.GetFullPath(reference);
        var relativePath = Path.GetRelativePath(workspaceDirectory, fullPath);
        if (Path.IsPathRooted(relativePath) || relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            return fullPath;
        return relativePath;
    }

    private static void Validate(WorkspaceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Version != WorkspaceDocument.CurrentVersion)
            throw new InvalidDataException($"Unsupported workspace version {document.Version}.");
        if (document.Submissions is null || document.Imports is null || document.References is null ||
            document.Packages is null || document.InputText is null)
            throw new InvalidDataException("Workspace contains missing fields.");
        if (document.Submissions.Count > 10_000 || document.Imports.Count > 1_000 ||
            document.References.Count > 10_000 || document.Packages.Count > 100)
            throw new InvalidDataException("Workspace contains too many entries.");
        if (document.Submissions.Any(static value => value is null) ||
            document.Imports.Any(static value => string.IsNullOrWhiteSpace(value)) ||
            document.References.Any(static value => string.IsNullOrWhiteSpace(value)) ||
            document.Packages.Any(static value => value is null || string.IsNullOrWhiteSpace(value.Id) || string.IsNullOrWhiteSpace(value.Version)))
            throw new InvalidDataException("Workspace contains invalid entries.");
    }
}
