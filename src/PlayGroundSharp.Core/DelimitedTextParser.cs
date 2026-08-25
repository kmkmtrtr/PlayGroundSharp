using System.Runtime.CompilerServices;
using System.Text;

namespace PlayGroundSharp.Core;

internal static class DelimitedTextParser
{
    public static async IAsyncEnumerable<string[]> ParseAsync(
        TextReader reader,
        char delimiter,
        int maximumColumns,
        int maximumFieldCharacters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var input = new BufferedCharacterReader(reader);
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var afterClosingQuote = false;
        var recordStarted = false;

        while (await input.ReadAsync(cancellationToken).ConfigureAwait(false) is { } character)
        {
            recordStarted = true;
            if (inQuotes)
            {
                if (character == '"')
                {
                    var next = await input.PeekAsync(cancellationToken).ConfigureAwait(false);
                    if (next == '"')
                    {
                        await input.ReadAsync(cancellationToken).ConfigureAwait(false);
                        Append(field, '"', maximumFieldCharacters);
                    }
                    else
                    {
                        inQuotes = false;
                        afterClosingQuote = true;
                    }
                }
                else
                {
                    Append(field, character, maximumFieldCharacters);
                }
                continue;
            }

            if (afterClosingQuote)
            {
                if (character == delimiter)
                {
                    AddField(fields, field, maximumColumns);
                    afterClosingQuote = false;
                    continue;
                }
                if (character is '\r' or '\n')
                {
                    if (character == '\r' && await input.PeekAsync(cancellationToken).ConfigureAwait(false) == '\n')
                        await input.ReadAsync(cancellationToken).ConfigureAwait(false);
                    AddField(fields, field, maximumColumns);
                    yield return [.. fields];
                    fields.Clear();
                    afterClosingQuote = false;
                    recordStarted = false;
                    continue;
                }
                if (character is ' ' or '\t') continue;
                throw new InvalidDataException("Unexpected character after a closing quote in delimited text.");
            }

            if (character == delimiter)
            {
                AddField(fields, field, maximumColumns);
                continue;
            }
            if (character is '\r' or '\n')
            {
                if (character == '\r' && await input.PeekAsync(cancellationToken).ConfigureAwait(false) == '\n')
                    await input.ReadAsync(cancellationToken).ConfigureAwait(false);
                AddField(fields, field, maximumColumns);
                yield return [.. fields];
                fields.Clear();
                recordStarted = false;
                continue;
            }
            if (character == '"' && field.Length == 0)
            {
                inQuotes = true;
                continue;
            }
            Append(field, character, maximumFieldCharacters);
        }

        if (inQuotes) throw new InvalidDataException("Delimited text ended inside a quoted field.");
        if (recordStarted || fields.Count > 0 || field.Length > 0)
        {
            AddField(fields, field, maximumColumns);
            yield return [.. fields];
        }
    }

    private static void Append(StringBuilder field, char value, int maximumFieldCharacters)
    {
        if (field.Length >= maximumFieldCharacters)
            throw new InvalidDataException($"A delimited field exceeded {maximumFieldCharacters:N0} characters.");
        field.Append(value);
    }

    private static void AddField(List<string> fields, StringBuilder field, int maximumColumns)
    {
        if (fields.Count >= maximumColumns)
            throw new InvalidDataException($"A delimited row exceeded {maximumColumns:N0} columns.");
        fields.Add(field.ToString());
        field.Clear();
    }

    private sealed class BufferedCharacterReader(TextReader reader)
    {
        private readonly char[] buffer = new char[4096];
        private int offset;
        private int count;

        public async ValueTask<char?> PeekAsync(CancellationToken cancellationToken)
        {
            if (!await EnsureBufferAsync(cancellationToken).ConfigureAwait(false)) return null;
            return buffer[offset];
        }

        public async ValueTask<char?> ReadAsync(CancellationToken cancellationToken)
        {
            if (!await EnsureBufferAsync(cancellationToken).ConfigureAwait(false)) return null;
            return buffer[offset++];
        }

        private async ValueTask<bool> EnsureBufferAsync(CancellationToken cancellationToken)
        {
            if (offset < count) return true;
            cancellationToken.ThrowIfCancellationRequested();
            count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            offset = 0;
            return count > 0;
        }
    }
}
