using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PlayGroundSharp.Core;

/// <summary>Exports a captured result as valid, indented JSON while retaining truncation metadata.</summary>
public static class SnapshotJsonFormatter
{
    private const string MetadataPropertyName = "$playgroundSharp";

    public static string Format(ResultSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var stream = new MemoryStream();
        var options = new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        using (var writer = new Utf8JsonWriter(stream, options))
            WriteSnapshot(writer, snapshot);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteSnapshot(Utf8JsonWriter writer, ResultSnapshot snapshot)
    {
        if (snapshot.Properties is not null)
        {
            writer.WriteStartObject();
            foreach (var property in snapshot.Properties)
            {
                writer.WritePropertyName(property.Name);
                WriteSnapshot(writer, property.Value);
            }
            if (snapshot.IsTruncated)
            {
                var metadataName = GetUniqueMetadataName(snapshot.Properties);
                WriteMetadata(writer, metadataName, snapshot, snapshot.Properties.Count);
            }
            writer.WriteEndObject();
            return;
        }

        if (snapshot.Items is not null)
        {
            writer.WriteStartArray();
            foreach (var item in snapshot.Items) WriteSnapshot(writer, item);
            if (snapshot.IsTruncated)
            {
                writer.WriteStartObject();
                WriteMetadata(writer, MetadataPropertyName, snapshot, snapshot.Items.Count);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            return;
        }

        if (!snapshot.IsTruncated)
        {
            WriteScalar(writer, snapshot);
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName("$value");
        WriteScalar(writer, snapshot);
        WriteMetadata(writer, MetadataPropertyName, snapshot, capturedCount: 1);
        writer.WriteEndObject();
    }

    private static void WriteScalar(Utf8JsonWriter writer, ResultSnapshot snapshot)
    {
        var display = snapshot.Display;
        switch (snapshot.Kind)
        {
            case SnapshotKind.Null:
                writer.WriteNullValue();
                return;
            case SnapshotKind.Boolean when bool.TryParse(display, out var boolean):
                writer.WriteBooleanValue(boolean);
                return;
            case SnapshotKind.Number when TryWriteNumber(writer, display):
                return;
            case SnapshotKind.Json when TryWriteJson(writer, display):
                return;
            default:
                writer.WriteStringValue(display ?? snapshot.Kind.ToString());
                return;
        }
    }

    private static bool TryWriteNumber(Utf8JsonWriter writer, string? display)
    {
        if (string.IsNullOrWhiteSpace(display)) return false;
        try
        {
            using var document = JsonDocument.Parse(display);
            if (document.RootElement.ValueKind != JsonValueKind.Number) return false;
            document.RootElement.WriteTo(writer);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryWriteJson(Utf8JsonWriter writer, string? display)
    {
        if (string.IsNullOrWhiteSpace(display)) return false;
        try
        {
            using var document = JsonDocument.Parse(display);
            document.RootElement.WriteTo(writer);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string GetUniqueMetadataName(IReadOnlyList<ResultProperty> properties)
    {
        var name = MetadataPropertyName;
        while (properties.Any(property => property.Name.Equals(name, StringComparison.Ordinal))) name += "$";
        return name;
    }

    private static void WriteMetadata(
        Utf8JsonWriter writer,
        string propertyName,
        ResultSnapshot snapshot,
        int capturedCount)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        writer.WriteBoolean("truncated", true);
        writer.WriteNumber("capturedCount", capturedCount);
        if (snapshot.TotalCount is { } totalCount) writer.WriteNumber("totalCount", totalCount);
        writer.WriteEndObject();
    }
}
