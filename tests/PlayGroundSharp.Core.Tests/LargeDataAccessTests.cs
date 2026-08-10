using System.Text.Json.Nodes;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.Core.Tests;

public sealed class LargeDataAccessTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"PlayGroundSharp-Data-{Guid.NewGuid():N}");

    public LargeDataAccessTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task ReadsLargeDataThroughBoundedAndStreamingOperations()
    {
        var textPath = Path.Combine(directory, "sample.txt");
        var jsonPath = Path.Combine(directory, "array.json");
        var jsonObjectPath = Path.Combine(directory, "object.json");
        var jsonLinesPath = Path.Combine(directory, "items.jsonl");
        await File.WriteAllTextAsync(textPath, "alpha\nbeta\ngamma");
        await File.WriteAllTextAsync(jsonPath, "[{\"id\":1},{\"id\":2},{\"id\":3}]");
        await File.WriteAllTextAsync(jsonObjectPath, "{\"name\":\"Ada\",\"scores\":[10,20]}");
        await File.WriteAllTextAsync(jsonLinesPath, "{\"id\":4}\n{\"id\":5}\n");
        var data = new LargeDataAccess();

        var info = data.Inspect(textPath);
        var preview = data.PreviewText(textPath, 5);
        var completeText = await data.ReadAllTextAsync(textPath);
        var completeBytes = await data.ReadAllBytesAsync(textPath);
        var lines = data.ReadLines(textPath).Take(2).ToArray();
        var array = await data.ReadJsonArrayAsync(jsonPath, 2);
        var jsonArray = Assert.IsType<JsonArray>(await data.ReadJsonAsync(jsonPath));
        var jsonObject = Assert.IsType<JsonObject>(await data.ReadJsonAsync(jsonObjectPath));
        var jsonLinesPreview = await data.ReadJsonLinesAsync(jsonLinesPath, 1);
        var jsonLines = new List<JsonNode?>();
        await foreach (var item in data.StreamJsonLinesAsync(jsonLinesPath)) jsonLines.Add(item);

        Assert.Equal(new FileInfo(textPath).Length, info.Length);
        Assert.Equal("alpha", preview);
        Assert.Equal("alpha\nbeta\ngamma", completeText);
        Assert.Equal(await File.ReadAllBytesAsync(textPath), completeBytes);
        Assert.Equal(["alpha", "beta"], lines);
        Assert.Equal([1, 2], array.Select(static item => item!["id"]!.GetValue<int>()));
        Assert.True(Assert.IsAssignableFrom<IBoundedSequenceResult>(array).HasMoreItems);
        Assert.Equal([1, 2, 3], jsonArray.Select(static item => item!["id"]!.GetValue<int>()));
        Assert.Equal("Ada", jsonObject["name"]!.GetValue<string>());
        Assert.Equal([10, 20], jsonObject["scores"]!.AsArray().Select(static item => item!.GetValue<int>()));
        Assert.Equal([4], jsonLinesPreview.Select(static item => item!["id"]!.GetValue<int>()));
        Assert.True(Assert.IsAssignableFrom<IBoundedSequenceResult>(jsonLinesPreview).HasMoreItems);
        Assert.Equal([4, 5], jsonLines.Select(static item => item!["id"]!.GetValue<int>()));
    }

    [Fact]
    public async Task JsonArrayReadDistinguishesExactAndLimitedBatches()
    {
        var path = Path.Combine(directory, "batch.json");
        await File.WriteAllTextAsync(path, "[1,2,3]");
        var data = new LargeDataAccess();

        var limited = await data.ReadJsonArrayAsync(path, 2);
        var exact = await data.ReadJsonArrayAsync(path, 3);

        Assert.Equal([1, 2], limited.Select(static item => item!.GetValue<int>()));
        Assert.True(Assert.IsAssignableFrom<IBoundedSequenceResult>(limited).HasMoreItems);
        Assert.Equal([1, 2, 3], exact.Select(static item => item!.GetValue<int>()));
        Assert.False(Assert.IsAssignableFrom<IBoundedSequenceResult>(exact).HasMoreItems);
    }

    [Fact]
    public async Task JsonLinesReadDistinguishesExactAndLimitedBatches()
    {
        var path = Path.Combine(directory, "batch.jsonl");
        await File.WriteAllTextAsync(path, "1\n\n2\n3\n");
        var data = new LargeDataAccess();

        var limited = await data.ReadJsonLinesAsync(path, 2);
        var exact = await data.ReadJsonLinesAsync(path, 3);
        var all = await data.ReadAllJsonLinesAsync(path);

        Assert.Equal([1, 2], limited.Select(static item => item!.GetValue<int>()));
        Assert.True(Assert.IsAssignableFrom<IBoundedSequenceResult>(limited).HasMoreItems);
        Assert.Equal([1, 2, 3], exact.Select(static item => item!.GetValue<int>()));
        Assert.False(Assert.IsAssignableFrom<IBoundedSequenceResult>(exact).HasMoreItems);
        Assert.Equal([1, 2, 3], all.Select(static item => item!.GetValue<int>()));
        Assert.IsNotAssignableFrom<IBoundedSequenceResult>(all);
    }

    [Fact]
    public void RejectsUnboundedReads()
    {
        var path = Path.Combine(directory, "sample.bin");
        File.WriteAllBytes(path, [1, 2, 3]);
        var data = new LargeDataAccess();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            data.ReadBytes(path, count: LargeDataAccess.MaximumByteReadCount + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            data.PreviewText(path, LargeDataAccess.MaximumPreviewCharacters + 1));
    }

    [Fact]
    public void BuildsSafePathAndJsonLinesSnippets()
    {
        var literal = DataSnippetBuilder.ToVerbatimStringLiteral("C:\\data\\a\"b.jsonl");
        var array = DataSnippetBuilder.CreatePathArray(["C:\\one.txt", "D:\\two.json"]);
        var jsonLines = DataSnippetBuilder.CreateJsonLines("C:\\data\\a\"b.jsonl");
        var allJsonLines = DataSnippetBuilder.CreateAllJsonLines("C:\\data\\a\"b.jsonl");
        var allText = DataSnippetBuilder.CreateAllText("C:\\data\\a\"b.jsonl");
        var lineStream = DataSnippetBuilder.CreateLineStream("C:\\data\\a\"b.jsonl");
        var allBytes = DataSnippetBuilder.CreateAllBytes("C:\\data\\a\"b.jsonl");
        var jsonArray = DataSnippetBuilder.CreateJsonArray("C:\\data\\items.json");
        var inspections = DataSnippetBuilder.CreateFileInspection(["C:\\one.txt", "D:\\two.json"]);
        var jsonBatch = DataSnippetBuilder.CreateJsonBatch(["C:\\one.json", "D:\\two.json"]);
        var mixedJsonBatch = DataSnippetBuilder.CreateJsonFilesBatch(
            ["C:\\one.json", "D:\\two.jsonl", "E:\\three.ndjson"]);
        var allMixedJsonBatch = DataSnippetBuilder.CreateJsonFilesBatch(
            ["C:\\one.json", "D:\\two.jsonl"],
            jsonLinesTake: null);
        var jsonLinesBatch = DataSnippetBuilder.CreateJsonLinesBatch(["C:\\one.data", "D:\\two.data"]);
        var textBatch = DataSnippetBuilder.CreateTextBatch(["C:\\one.data", "D:\\two.data"]);
        var lineStreams = DataSnippetBuilder.CreateLineStreams(["C:\\one.data", "D:\\two.data"]);
        var bytesBatch = DataSnippetBuilder.CreateBytesBatch(["C:\\one.data", "D:\\two.data"]);

        Assert.Equal("@\"C:\\data\\a\"\"b.jsonl\"", literal);
        Assert.Contains("@\"C:\\one.txt\"", array, StringComparison.Ordinal);
        Assert.Contains("@\"D:\\two.json\"", array, StringComparison.Ordinal);
        Assert.Equal($"await Data.ReadJsonLinesAsync({literal}, 1000, ExecutionCancellation)", jsonLines);
        Assert.DoesNotContain("await foreach", jsonLines, StringComparison.Ordinal);
        Assert.Equal($"await Data.ReadAllJsonLinesAsync({literal}, ExecutionCancellation)", allJsonLines);
        Assert.Equal($"await Data.ReadAllTextAsync({literal}, ExecutionCancellation)", allText);
        Assert.Equal($"Data.ReadLines({literal})", lineStream);
        Assert.Equal($"await Data.ReadAllBytesAsync({literal}, ExecutionCancellation)", allBytes);
        Assert.Contains("ReadJsonArrayAsync(@\"C:\\data\\items.json\", 1000", jsonArray, StringComparison.Ordinal);
        Assert.Contains("Select(path => Data.Inspect(path))", inspections, StringComparison.Ordinal);
        Assert.Contains("@\"C:\\one.txt\"", inspections, StringComparison.Ordinal);
        Assert.Contains("foreach (var path", jsonBatch, StringComparison.Ordinal);
        Assert.Contains("new List<JsonNode?>()", jsonBatch, StringComparison.Ordinal);
        Assert.Contains("jsonValues.Add(await Data.ReadJsonAsync(path, ExecutionCancellation))", jsonBatch, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.WhenAll", jsonBatch, StringComparison.Ordinal);
        Assert.Contains("jsonValues.Add(await Data.ReadJsonAsync(@\"C:\\one.json\", ExecutionCancellation))", mixedJsonBatch, StringComparison.Ordinal);
        Assert.Contains("jsonValues.AddRange(await Data.ReadJsonLinesAsync(@\"D:\\two.jsonl\", 1000, ExecutionCancellation))", mixedJsonBatch, StringComparison.Ordinal);
        Assert.Contains("jsonValues.AddRange(await Data.ReadJsonLinesAsync(@\"E:\\three.ndjson\", 1000, ExecutionCancellation))", mixedJsonBatch, StringComparison.Ordinal);
        Assert.Contains("jsonValues.AddRange(await Data.ReadAllJsonLinesAsync(@\"D:\\two.jsonl\", ExecutionCancellation))", allMixedJsonBatch, StringComparison.Ordinal);
        Assert.Contains("jsonLineFiles.Add(await Data.ReadAllJsonLinesAsync(path, ExecutionCancellation))", jsonLinesBatch, StringComparison.Ordinal);
        Assert.Contains("textFiles.Add(await Data.ReadAllTextAsync(path, ExecutionCancellation))", textBatch, StringComparison.Ordinal);
        Assert.Contains("new { Path = path, Lines = Data.ReadLines(path) }", lineStreams, StringComparison.Ordinal);
        Assert.Contains("binaryFiles.Add(await Data.ReadAllBytesAsync(path, ExecutionCancellation))", bytesBatch, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
