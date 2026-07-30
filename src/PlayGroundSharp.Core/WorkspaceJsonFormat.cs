using System.Text.Json;

namespace PlayGroundSharp.Core;

/// <summary>Creates consistent, human-readable JSON options for portable workspace files.</summary>
public static class WorkspaceJsonFormat
{
    public static JsonSerializerOptions CreateOptions(
        JsonSerializerDefaults defaults = JsonSerializerDefaults.General) =>
        new(defaults)
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = ReadableJsonEncoder.Instance
        };
}
