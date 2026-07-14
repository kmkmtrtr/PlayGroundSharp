using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace PlayGroundSharp.Core;

/// <summary>Describes a file without reading its contents into memory.</summary>
public sealed record FileProbe(string FullPath, long Length, DateTime LastWriteTimeUtc, string Extension);

/// <summary>Provides bounded and streaming helpers for inspecting large local files from submissions.</summary>
public sealed class LargeDataAccess
{
    public const int MaximumPreviewCharacters = 1_048_576;
    public const int MaximumByteReadCount = 1_048_576;
    public const int MaximumJsonItemCount = 10_000;

    /// <summary>Reads one complete JSON value such as an object, scalar, or array.</summary>
    public async Task<JsonElement> ReadJsonAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var file = GetFile(path);
        await using var stream = file.OpenRead();
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    /// <summary>Returns metadata for an existing file without loading its content.</summary>
    public FileProbe Inspect(string path)
    {
        var file = GetFile(path);
        return new(file.FullName, file.Length, file.LastWriteTimeUtc, file.Extension);
    }

    /// <summary>Returns a lazy line sequence. Enumeration opens and streams the file.</summary>
    public IEnumerable<string> ReadLines(string path)
    {
        var file = GetFile(path);
        return File.ReadLines(file.FullName);
    }

    /// <summary>Reads only the beginning of a text file, bounded to one MiB of characters.</summary>
    public string PreviewText(string path, int maxCharacters = 65_536, Encoding? encoding = null)
    {
        var file = GetFile(path);
        var count = ValidateRange(maxCharacters, 1, MaximumPreviewCharacters, nameof(maxCharacters));
        using var reader = new StreamReader(file.FullName, encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[count];
        var read = reader.ReadBlock(buffer, 0, count);
        return new(buffer, 0, read);
    }

    /// <summary>Reads a bounded byte range without loading the whole file.</summary>
    public byte[] ReadBytes(string path, long offset = 0, int count = 65_536)
    {
        var file = GetFile(path);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var boundedCount = ValidateRange(count, 1, MaximumByteReadCount, nameof(count));
        using var stream = file.OpenRead();
        if (offset > stream.Length) throw new ArgumentOutOfRangeException(nameof(offset), "Offset exceeds file length.");
        stream.Position = offset;
        var buffer = new byte[Math.Min(boundedCount, checked((int)Math.Min(int.MaxValue, stream.Length - offset)))];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return read == buffer.Length ? buffer : buffer[..read];
    }

    /// <summary>Streams a top-level JSON array and retains only the requested number of elements.</summary>
    public async Task<IReadOnlyList<JsonElement>> ReadJsonArrayAsync(
        string path,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        var file = GetFile(path);
        var boundedTake = ValidateRange(take, 1, MaximumJsonItemCount, nameof(take));
        await using var stream = file.OpenRead();
        var items = new List<JsonElement>(Math.Min(boundedTake, 256));
        await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(stream, cancellationToken: cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            items.Add(item.Clone());
            if (items.Count >= boundedTake) break;
        }
        return items;
    }

    /// <summary>Streams newline-delimited JSON and parses one independent value at a time.</summary>
    public async IAsyncEnumerable<JsonElement> ReadJsonLinesAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var file = GetFile(path);
        using var reader = new StreamReader(file.FullName, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            yield return document.RootElement.Clone();
        }
    }

    private static FileInfo GetFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists) throw new FileNotFoundException("File was not found.", file.FullName);
        return file;
    }

    private static int ValidateRange(int value, int minimum, int maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(parameterName, $"Value must be between {minimum:N0} and {maximum:N0}.");
        return value;
    }
}
