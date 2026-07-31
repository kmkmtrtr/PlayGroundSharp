using System.Text.Encodings.Web;

namespace PlayGroundSharp.Core;

/// <summary>
/// Uses relaxed JSON escaping while keeping the ideographic space (U+3000) readable.
/// </summary>
public sealed class ReadableJsonEncoder : JavaScriptEncoder
{
    private readonly JavaScriptEncoder fallback = UnsafeRelaxedJsonEscaping;

    private ReadableJsonEncoder()
    {
    }

    public static ReadableJsonEncoder Instance { get; } = new();

    public override int MaxOutputCharactersPerInputCharacter =>
        fallback.MaxOutputCharactersPerInputCharacter;

    public override bool WillEncode(int unicodeScalar) =>
        unicodeScalar != '\u3000' && fallback.WillEncode(unicodeScalar);

    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(textLength);
        if (text == null)
        {
            if (textLength == 0) return -1;
            throw new ArgumentNullException(nameof(text));
        }

        for (var index = 0; index < textLength; index++)
        {
            var scalar = (int)text[index];
            if (char.IsHighSurrogate((char)scalar) &&
                index + 1 < textLength &&
                char.IsLowSurrogate(text[index + 1]))
            {
                scalar = char.ConvertToUtf32((char)scalar, text[index + 1]);
                if (WillEncode(scalar)) return index;
                index++;
                continue;
            }
            if (WillEncode(scalar)) return index;
        }
        return -1;
    }

    public override unsafe bool TryEncodeUnicodeScalar(
        int unicodeScalar,
        char* buffer,
        int bufferLength,
        out int numberOfCharactersWritten)
    {
        if (unicodeScalar != '\u3000')
            return fallback.TryEncodeUnicodeScalar(
                unicodeScalar,
                buffer,
                bufferLength,
                out numberOfCharactersWritten);

        numberOfCharactersWritten = 0;
        if (bufferLength < 1) return false;
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        buffer[0] = '\u3000';
        numberOfCharactersWritten = 1;
        return true;
    }
}
