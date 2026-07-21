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

    public static string CreateJsonLines(string path)
    {
        var pathLiteral = ToVerbatimStringLiteral(path);
        return $"var rows = new List<JsonElement>();{Environment.NewLine}" +
               $"await foreach (var row in Data.ReadJsonLinesAsync({pathLiteral}, ExecutionCancellation)){Environment.NewLine}" +
               $"{{{Environment.NewLine}    rows.Add(row);{Environment.NewLine}" +
               $"    if (rows.Count == 100) break;{Environment.NewLine}}}{Environment.NewLine}rows";
    }

    public static string CreateFileInspection(IReadOnlyList<string> paths) =>
        $"({CreatePathArray(paths)}){Environment.NewLine}" +
        $".Select(path => Data.Inspect(path)){Environment.NewLine}.ToArray()";

    public static string CreateJsonBatch(IReadOnlyList<string> paths) =>
        $"var jsonValues = new List<JsonElement>();{Environment.NewLine}" +
        $"foreach (var path in {CreatePathArray(paths)}){Environment.NewLine}" +
        $"{{{Environment.NewLine}" +
        $"    jsonValues.Add(await Data.ReadJsonAsync(path, ExecutionCancellation));{Environment.NewLine}" +
        $"}}{Environment.NewLine}" +
        "jsonValues";
}
