using System.Text.Json;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.Core.Tests;

public sealed class ReadableJsonEncoderTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = ReadableJsonEncoder.Instance
    };

    [Fact]
    public void KeepsFullWidthSpacesWhileEscapingJsonSyntax()
    {
        const string value = "quote\" slash\\ line\nwide　emoji😀";

        var serialized = JsonSerializer.Serialize(value, Options);

        Assert.Contains("wide　emoji", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u3000", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(value, JsonSerializer.Deserialize<string>(serialized));
    }

    [Fact]
    public void DoesNotConfuseLiteralUnicodeEscapesWithFullWidthSpaces()
    {
        const string value = @"literal \u3000 and actual　space";

        var serialized = JsonSerializer.Serialize(value, Options);

        Assert.Contains(@"\\u3000", serialized, StringComparison.Ordinal);
        Assert.Contains("actual　space", serialized, StringComparison.Ordinal);
        Assert.Equal(value, JsonSerializer.Deserialize<string>(serialized));
    }

    [Fact]
    public void KeepsFullWidthSpacesInPropertyNames()
    {
        var value = new Dictionary<string, string>
        {
            ["full　width"] = "value　text"
        };

        var serialized = JsonSerializer.Serialize(value, Options);
        using var document = JsonDocument.Parse(serialized);

        Assert.Contains("\"full　width\":\"value　text\"", serialized, StringComparison.Ordinal);
        Assert.Equal("value　text", document.RootElement.GetProperty("full　width").GetString());
    }
}
