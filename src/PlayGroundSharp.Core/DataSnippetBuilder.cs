namespace PlayGroundSharp.Core;

/// <summary>Builds C# input snippets for local paths selected by the desktop UI.</summary>
public static class DataSnippetBuilder
{
    public static string ToVerbatimStringLiteral(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return $"@\"{path.Replace("\"", "\"\"")}\"";
    }

    public static string CreatePathArray(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return "new[]" + Environment.NewLine + "{" + Environment.NewLine +
               string.Join("," + Environment.NewLine,
                   paths.Select(path => "    " + ToVerbatimStringLiteral(path))) +
               Environment.NewLine + "}";
    }

    public static string CreateJsonLines(
        string path,
        int take = LargeDataAccess.DefaultJsonPreviewItemCount)
    {
        if (take is < 1 or > LargeDataAccess.MaximumJsonItemCount)
            throw new ArgumentOutOfRangeException(nameof(take));
        var pathLiteral = ToVerbatimStringLiteral(path);
        return $"await Data.ReadJsonLinesAsync({pathLiteral}, {take}, ExecutionCancellation)";
    }

    public static string CreateAllJsonLines(string path) =>
        $"await Data.ReadAllJsonLinesAsync({ToVerbatimStringLiteral(path)}, ExecutionCancellation)";

    public static string CreateJsonArray(
        string path,
        int take = LargeDataAccess.DefaultJsonPreviewItemCount)
    {
        if (take is < 1 or > LargeDataAccess.MaximumJsonItemCount)
            throw new ArgumentOutOfRangeException(nameof(take));
        return $"await Data.ReadJsonArrayAsync({ToVerbatimStringLiteral(path)}, {take}, ExecutionCancellation)";
    }

    public static string CreateFileInspection(IReadOnlyList<string> paths) =>
        $"({CreatePathArray(paths)}){Environment.NewLine}" +
        $".Select(path => Data.Inspect(path)){Environment.NewLine}.ToArray()";

    public static string CreateJsonBatch(IReadOnlyList<string> paths) =>
        $"var jsonValues = new List<JsonNode?>();{Environment.NewLine}" +
        $"foreach (var path in {CreatePathArray(paths)}){Environment.NewLine}" +
        $"{{{Environment.NewLine}" +
        $"    jsonValues.Add(await Data.ReadJsonAsync(path, ExecutionCancellation));{Environment.NewLine}" +
        $"}}{Environment.NewLine}" +
        "jsonValues";

    public static string CreateJsonFilesBatch(
        IReadOnlyList<string> paths,
        int? jsonLinesTake = LargeDataAccess.DefaultJsonPreviewItemCount)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0) throw new ArgumentException("At least one path is required.", nameof(paths));
        if (jsonLinesTake is < 1 or > LargeDataAccess.MaximumJsonItemCount)
            throw new ArgumentOutOfRangeException(nameof(jsonLinesTake));

        var statements = paths.Select(path => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" =>
                $"jsonValues.Add(await Data.ReadJsonAsync({ToVerbatimStringLiteral(path)}, ExecutionCancellation));",
            ".jsonl" or ".ndjson" =>
                jsonLinesTake is { } take
                    ? $"jsonValues.AddRange(await Data.ReadJsonLinesAsync({ToVerbatimStringLiteral(path)}, {take}, ExecutionCancellation));"
                    : $"jsonValues.AddRange(await Data.ReadAllJsonLinesAsync({ToVerbatimStringLiteral(path)}, ExecutionCancellation));",
            _ => throw new ArgumentException("Only .json, .jsonl, and .ndjson paths are supported.", nameof(paths))
        });
        return $"var jsonValues = new List<JsonNode?>();{Environment.NewLine}" +
               string.Join(Environment.NewLine, statements) + Environment.NewLine +
               "jsonValues";
    }
}
