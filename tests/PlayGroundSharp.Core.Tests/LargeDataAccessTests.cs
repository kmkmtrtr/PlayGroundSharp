using System.Text.Json;
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
        var jsonLinesPath = Path.Combine(directory, "items.jsonl");
        await File.WriteAllTextAsync(textPath, "alpha\nbeta\ngamma");
        await File.WriteAllTextAsync(jsonPath, "[{\"id\":1},{\"id\":2},{\"id\":3}]");
        await File.WriteAllTextAsync(jsonLinesPath, "{\"id\":4}\n{\"id\":5}\n");
        var data = new LargeDataAccess();

        var info = data.Inspect(textPath);
        var preview = data.PreviewText(textPath, 5);
        var lines = data.ReadLines(textPath).Take(2).ToArray();
        var array = await data.ReadJsonArrayAsync(jsonPath, 2);
        var jsonLines = new List<JsonElement>();
        await foreach (var item in data.ReadJsonLinesAsync(jsonLinesPath)) jsonLines.Add(item);

        Assert.Equal(new FileInfo(textPath).Length, info.Length);
        Assert.Equal("alpha", preview);
        Assert.Equal(["alpha", "beta"], lines);
        Assert.Equal([1, 2], array.Select(static item => item.GetProperty("id").GetInt32()));
        Assert.Equal([4, 5], jsonLines.Select(static item => item.GetProperty("id").GetInt32()));
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

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
