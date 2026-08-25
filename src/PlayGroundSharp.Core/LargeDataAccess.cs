using System.Collections;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlayGroundSharp.Core;

/// <summary>Describes a file without reading its contents into memory.</summary>
public sealed record FileProbe(string FullPath, long Length, DateTime LastWriteTimeUtc, string Extension);

/// <summary>Marks a materialized sequence that intentionally retained only its leading items.</summary>
internal interface IBoundedSequenceResult
{
    bool HasMoreItems { get; }
}

/// <summary>A read-only JSON batch that records whether the source array contained more items.</summary>
internal sealed class BoundedJsonNodeList(
    IReadOnlyList<JsonNode?> items,
    bool hasMoreItems) : IReadOnlyList<JsonNode?>, IBoundedSequenceResult
{
    public bool HasMoreItems { get; } = hasMoreItems;
    public int Count => items.Count;
    public JsonNode? this[int index] => items[index];
    public IEnumerator<JsonNode?> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A read-only delimited-text batch that records whether the source contained more rows.</summary>
internal sealed class BoundedDelimitedRowList(
    IReadOnlyList<IReadOnlyDictionary<string, string?>> rows,
    bool hasMoreItems) : IReadOnlyList<IReadOnlyDictionary<string, string?>>, IBoundedSequenceResult
{
    public bool HasMoreItems { get; } = hasMoreItems;
    public int Count => rows.Count;
    public IReadOnlyDictionary<string, string?> this[int index] => rows[index];
    public IEnumerator<IReadOnlyDictionary<string, string?>> GetEnumerator() => rows.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Provides bounded and streaming helpers for inspecting large local files from submissions.</summary>
public sealed class LargeDataAccess
{
    public const int MaximumPreviewCharacters = 1_048_576;
    public const int MaximumByteReadCount = 1_048_576;
    public const int MaximumJsonItemCount = 10_000;
    public const int DefaultJsonPreviewItemCount = 1_000;
    public const int MaximumDelimitedRowCount = 10_000;
    public const int MaximumDelimitedColumnCount = 4_096;
    public const int MaximumDelimitedFieldCharacters = 1_048_576;

    /// <summary>Reads one complete JSON value such as an object, scalar, or array.</summary>
    public async Task<JsonNode?> ReadJsonAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var file = GetFile(path);
        await using var stream = file.OpenRead();
        return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
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

    /// <summary>Reads the complete file as UTF-8 text.</summary>
    /// <remarks>Use <see cref="ReadLines"/> or <see cref="PreviewText"/> when the complete content need not be retained.</remarks>
    public async Task<string> ReadAllTextAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var file = GetFile(path);
        return await File.ReadAllTextAsync(file.FullName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the complete file as a byte array.</summary>
    /// <remarks>Use <see cref="ReadBytes"/> when only a bounded range is needed.</remarks>
    public async Task<byte[]> ReadAllBytesAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var file = GetFile(path);
        return await File.ReadAllBytesAsync(file.FullName, cancellationToken).ConfigureAwait(false);
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
    public async Task<IReadOnlyList<JsonNode?>> ReadJsonArrayAsync(
        string path,
        int take = DefaultJsonPreviewItemCount,
        CancellationToken cancellationToken = default)
    {
        var file = GetFile(path);
        var boundedTake = ValidateRange(take, 1, MaximumJsonItemCount, nameof(take));
        await using var stream = file.OpenRead();
        var items = new List<JsonNode?>(Math.Min(boundedTake, 256));
        var hasMoreItems = false;
        await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<JsonNode?>(stream, cancellationToken: cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (items.Count >= boundedTake)
            {
                hasMoreItems = true;
                break;
            }
            items.Add(item);
        }
        return new BoundedJsonNodeList(items, hasMoreItems);
    }

    /// <summary>Reads the leading values from newline-delimited JSON without loading the whole file.</summary>
    public async Task<IReadOnlyList<JsonNode?>> ReadJsonLinesAsync(
        string path,
        int take,
        CancellationToken cancellationToken = default)
    {
        var boundedTake = ValidateRange(take, 1, MaximumJsonItemCount, nameof(take));
        var items = new List<JsonNode?>(Math.Min(boundedTake, 256));
        var hasMoreItems = false;
        await foreach (var item in StreamJsonLinesAsync(path, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (items.Count >= boundedTake)
            {
                hasMoreItems = true;
                break;
            }
            items.Add(item);
        }
        return new BoundedJsonNodeList(items, hasMoreItems);
    }

    /// <summary>Reads every value from newline-delimited JSON into memory.</summary>
    /// <remarks>Prefer the bounded overload or <see cref="StreamJsonLinesAsync"/> for large files.</remarks>
    public async Task<IReadOnlyList<JsonNode?>> ReadAllJsonLinesAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var items = new List<JsonNode?>();
        await foreach (var item in StreamJsonLinesAsync(path, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
            items.Add(item);
        return items;
    }

    /// <summary>Compatibility overload that streams newline-delimited JSON.</summary>
    public IAsyncEnumerable<JsonNode?> ReadJsonLinesAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        StreamJsonLinesAsync(path, cancellationToken);

    /// <summary>Streams newline-delimited JSON and parses one independent value at a time.</summary>
    public async IAsyncEnumerable<JsonNode?> StreamJsonLinesAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var file = GetFile(path);
        using var reader = new StreamReader(file.FullName, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            yield return JsonNode.Parse(line);
        }
    }

    /// <summary>Reads every row of a comma-separated file.</summary>
    public Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> ReadCsvAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        ReadDelimitedAsync(path, ',', hasHeader: true, take: null, cancellationToken);

    /// <summary>Reads comma-separated rows, optionally retaining only a leading batch.</summary>
    public Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> ReadCsvAsync(
        string path,
        bool hasHeader,
        int? take = null,
        CancellationToken cancellationToken = default) =>
        ReadDelimitedAsync(path, ',', hasHeader, take, cancellationToken);

    /// <summary>Reads rows using a configurable CSV delimiter.</summary>
    public Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> ReadCsvAsync(
        string path,
        char delimiter,
        bool hasHeader = true,
        int? take = null,
        CancellationToken cancellationToken = default) =>
        ReadDelimitedAsync(path, delimiter, hasHeader, take, cancellationToken);

    /// <summary>Reads every row of a tab-separated file.</summary>
    public Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> ReadTsvAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        ReadDelimitedAsync(path, '\t', hasHeader: true, take: null, cancellationToken);

    /// <summary>Reads tab-separated rows, optionally retaining only a leading batch.</summary>
    public Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> ReadTsvAsync(
        string path,
        bool hasHeader,
        int? take = null,
        CancellationToken cancellationToken = default) =>
        ReadDelimitedAsync(path, '\t', hasHeader, take, cancellationToken);

    /// <summary>Reads all delimited rows unless a leading row count is specified.</summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> ReadDelimitedAsync(
        string path,
        char delimiter,
        bool hasHeader,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        if (take is null)
            return await ReadAllDelimitedAsync(path, delimiter, hasHeader, cancellationToken).ConfigureAwait(false);

        var boundedTake = ValidateRange(take.Value, 1, MaximumDelimitedRowCount, nameof(take));
        var rows = new List<IReadOnlyDictionary<string, string?>>(Math.Min(boundedTake, 256));
        var hasMoreItems = false;
        await foreach (var row in StreamDelimitedAsync(path, delimiter, hasHeader, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count >= boundedTake)
            {
                hasMoreItems = true;
                break;
            }
            rows.Add(row);
        }
        return new BoundedDelimitedRowList(rows, hasMoreItems);
    }

    /// <summary>Reads every row of a comma-separated file into memory.</summary>
    public Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> ReadAllCsvAsync(
        string path,
        CancellationToken cancellationToken) =>
        ReadAllDelimitedAsync(path, ',', hasHeader: true, cancellationToken);

    /// <summary>Reads every row of a comma-separated file into memory.</summary>
    public Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> ReadAllCsvAsync(
        string path,
        bool hasHeader = true,
        CancellationToken cancellationToken = default) =>
        ReadAllDelimitedAsync(path, ',', hasHeader, cancellationToken);

    /// <summary>Reads every row of a tab-separated file into memory.</summary>
    public Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> ReadAllTsvAsync(
        string path,
        CancellationToken cancellationToken) =>
        ReadAllDelimitedAsync(path, '\t', hasHeader: true, cancellationToken);

    /// <summary>Reads every row of a tab-separated file into memory.</summary>
    public Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> ReadAllTsvAsync(
        string path,
        bool hasHeader = true,
        CancellationToken cancellationToken = default) =>
        ReadAllDelimitedAsync(path, '\t', hasHeader, cancellationToken);

    /// <summary>Reads every row of a delimited text file into memory.</summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> ReadAllDelimitedAsync(
        string path,
        char delimiter,
        bool hasHeader,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        await foreach (var row in StreamDelimitedAsync(path, delimiter, hasHeader, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
            rows.Add(row);
        return rows;
    }

    /// <summary>Streams rows from a comma-separated file without retaining every row.</summary>
    public IAsyncEnumerable<IReadOnlyDictionary<string, string?>> StreamCsvAsync(
        string path,
        CancellationToken cancellationToken) =>
        StreamDelimitedAsync(path, ',', hasHeader: true, cancellationToken);

    /// <summary>Streams rows from a comma-separated file without retaining every row.</summary>
    public IAsyncEnumerable<IReadOnlyDictionary<string, string?>> StreamCsvAsync(
        string path,
        bool hasHeader = true,
        CancellationToken cancellationToken = default) =>
        StreamDelimitedAsync(path, ',', hasHeader, cancellationToken);

    /// <summary>Streams rows from a tab-separated file without retaining every row.</summary>
    public IAsyncEnumerable<IReadOnlyDictionary<string, string?>> StreamTsvAsync(
        string path,
        CancellationToken cancellationToken) =>
        StreamDelimitedAsync(path, '\t', hasHeader: true, cancellationToken);

    /// <summary>Streams rows from a tab-separated file without retaining every row.</summary>
    public IAsyncEnumerable<IReadOnlyDictionary<string, string?>> StreamTsvAsync(
        string path,
        bool hasHeader = true,
        CancellationToken cancellationToken = default) =>
        StreamDelimitedAsync(path, '\t', hasHeader, cancellationToken);

    /// <summary>Streams rows from a delimited text file without retaining every row.</summary>
    public async IAsyncEnumerable<IReadOnlyDictionary<string, string?>> StreamDelimitedAsync(
        string path,
        char delimiter,
        bool hasHeader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateDelimiter(delimiter);
        var file = GetFile(path);
        using var reader = new StreamReader(file.FullName, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string[]? columns = null;
        await foreach (var fields in DelimitedTextParser.ParseAsync(
                           reader,
                           delimiter,
                           MaximumDelimitedColumnCount,
                           MaximumDelimitedFieldCharacters,
                           cancellationToken).ConfigureAwait(false))
        {
            if (columns is null)
            {
                columns = hasHeader ? CreateColumnNames(fields) : CreateColumnNames([], fields.Length);
                if (hasHeader) continue;
            }
            if (fields.Length > columns.Length)
                columns = CreateColumnNames(columns, fields.Length);
            yield return CreateDelimitedRow(columns, fields);
        }
    }

    private static IReadOnlyDictionary<string, string?> CreateDelimitedRow(
        IReadOnlyList<string> columns,
        IReadOnlyList<string> fields)
    {
        var row = new Dictionary<string, string?>(columns.Count, StringComparer.Ordinal);
        for (var index = 0; index < columns.Count; index++)
            row.Add(columns[index], index < fields.Count ? fields[index] : null);
        return row;
    }

    private static string[] CreateColumnNames(IReadOnlyList<string> source, int minimumCount = 0)
    {
        var count = Math.Max(source.Count, minimumCount);
        var names = new string[count];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < count; index++)
        {
            var basis = index < source.Count && !string.IsNullOrWhiteSpace(source[index])
                ? source[index].Trim()
                : $"Column{index + 1}";
            var name = basis;
            var suffix = 2;
            while (!used.Add(name)) name = basis + suffix++;
            names[index] = name;
        }
        return names;
    }

    private static void ValidateDelimiter(char delimiter)
    {
        if (delimiter is '\0' or '\r' or '\n' or '"')
            throw new ArgumentOutOfRangeException(nameof(delimiter), "Delimiter cannot be NUL, a line break, or a double quote.");
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
