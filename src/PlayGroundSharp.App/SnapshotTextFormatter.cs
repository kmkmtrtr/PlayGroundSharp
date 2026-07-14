using System.Text;
using System.Text.Json;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

internal sealed record SnapshotFormatResult(string Text, bool IsLimited);

/// <summary>Formats process-neutral snapshots as readable, copyable structured text.</summary>
internal static class SnapshotTextFormatter
{
    private const int PreviewCharacterLimit = 20_000;
    private const int PreviewItemLimit = 200;

    public static SnapshotFormatResult FormatPreview(ResultSnapshot snapshot) =>
        Format(snapshot, PreviewCharacterLimit, PreviewItemLimit);

    public static string FormatFull(ResultSnapshot snapshot) =>
        Format(snapshot, int.MaxValue, int.MaxValue).Text;

    public static string FormatCompact(ResultSnapshot snapshot)
    {
        if (snapshot.Items is not null)
            return $"[{snapshot.TotalCount?.ToString("N0") ?? snapshot.Items.Count.ToString("N0")} items]" +
                   (snapshot.IsTruncated ? " …" : string.Empty);
        if (snapshot.Properties is not null)
            return $"{{{snapshot.TotalCount?.ToString("N0") ?? snapshot.Properties.Count.ToString("N0")} properties}}" +
                   (snapshot.IsTruncated ? " …" : string.Empty);
        return (snapshot.Display ?? snapshot.Kind.ToString()) + (snapshot.IsTruncated ? " …" : string.Empty);
    }

    private static SnapshotFormatResult Format(ResultSnapshot snapshot, int characterLimit, int itemLimit)
    {
        var writer = new SnapshotWriter(characterLimit, itemLimit);
        writer.Write(snapshot, 0);
        return new(writer.Text, writer.IsLimited);
    }

    private sealed class SnapshotWriter(int characterLimit, int itemLimit)
    {
        private readonly StringBuilder builder = new(Math.Min(characterLimit, 16_384));
        private int writtenItems;
        private bool characterLimitReached;
        private bool itemLimitReached;

        public bool IsLimited => characterLimitReached || itemLimitReached;
        public string Text => builder.ToString();

        public void Write(ResultSnapshot snapshot, int depth)
        {
            if (characterLimitReached) return;
            if (snapshot.Properties is not null)
            {
                WriteProperties(snapshot, depth);
                return;
            }
            if (snapshot.Items is not null)
            {
                WriteItems(snapshot, depth);
                return;
            }
            WriteScalar(snapshot);
        }

        private void WriteProperties(ResultSnapshot snapshot, int depth)
        {
            Append("{");
            var written = 0;
            foreach (var property in snapshot.Properties!)
            {
                if (!CanWriteItem()) break;
                Append(written++ == 0 ? Environment.NewLine : "," + Environment.NewLine);
                AppendIndent(depth + 1);
                Append(JsonSerializer.Serialize(property.Name));
                Append(": ");
                Write(property.Value, depth + 1);
                if (characterLimitReached || itemLimitReached) break;
            }
            WriteRemainder(snapshot, snapshot.Properties!.Count, depth + 1, written);
            if (written > 0 || snapshot.IsTruncated)
            {
                Append(Environment.NewLine);
                AppendIndent(depth);
            }
            Append("}");
        }

        private void WriteItems(ResultSnapshot snapshot, int depth)
        {
            Append("[");
            var written = 0;
            foreach (var item in snapshot.Items!)
            {
                if (!CanWriteItem()) break;
                Append(written++ == 0 ? Environment.NewLine : "," + Environment.NewLine);
                AppendIndent(depth + 1);
                Write(item, depth + 1);
                if (characterLimitReached || itemLimitReached) break;
            }
            WriteRemainder(snapshot, snapshot.Items!.Count, depth + 1, written);
            if (written > 0 || snapshot.IsTruncated)
            {
                Append(Environment.NewLine);
                AppendIndent(depth);
            }
            Append("]");
        }

        private void WriteRemainder(ResultSnapshot snapshot, int capturedCount, int depth, int writtenInCollection)
        {
            if (characterLimitReached || !snapshot.IsTruncated && writtenInCollection >= capturedCount) return;
            Append(writtenInCollection == 0 ? Environment.NewLine : "," + Environment.NewLine);
            AppendIndent(depth);
            var remaining = snapshot.TotalCount is { } total
                ? Math.Max(0, total - writtenInCollection).ToString("N0") + " more"
                : Math.Max(0, capturedCount - writtenInCollection).ToString("N0") + " or more";
            Append($"… ({remaining} not captured)");
        }

        private void WriteScalar(ResultSnapshot snapshot)
        {
            var display = snapshot.Display ?? snapshot.Kind.ToString();
            if (snapshot.Kind is SnapshotKind.String or SnapshotKind.DateTime or SnapshotKind.Guid)
            {
                var remaining = Math.Max(0, characterLimit - builder.Length - 2);
                var value = display.Length <= remaining ? display : display[..remaining];
                Append(JsonSerializer.Serialize(value));
                if (value.Length < display.Length) characterLimitReached = true;
            }
            else
                Append(display);
            if (snapshot.IsTruncated) Append(" …");
        }

        private bool CanWriteItem()
        {
            if (writtenItems < itemLimit)
            {
                writtenItems++;
                return true;
            }
            itemLimitReached = true;
            return false;
        }

        private void AppendIndent(int depth) => Append(new string(' ', depth * 2));

        private void Append(string value)
        {
            if (characterLimitReached || value.Length == 0) return;
            var remaining = characterLimit - builder.Length;
            if (remaining <= 0)
            {
                characterLimitReached = true;
                return;
            }
            if (value.Length <= remaining)
            {
                builder.Append(value);
                return;
            }
            builder.Append(value.AsSpan(0, remaining));
            characterLimitReached = true;
        }
    }
}
