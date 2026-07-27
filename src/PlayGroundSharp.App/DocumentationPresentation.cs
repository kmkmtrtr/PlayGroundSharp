using System.Text.RegularExpressions;

namespace PlayGroundSharp.App;

internal sealed record DocumentationParameterPresentation(
    string Name,
    string TypeName,
    string Summary,
    bool IsActive);

internal sealed record DocumentationPresentation(
    string Signature,
    string Summary,
    IReadOnlyList<DocumentationParameterPresentation> Parameters,
    string Returns,
    string Notice)
{
    public static DocumentationPresentation Message(string message) =>
        new(string.Empty, message, [], string.Empty, string.Empty);
}

internal static partial class DocumentationPresentationParser
{
    public static DocumentationPresentation Parse(
        string? text,
        string fallbackSignature = "",
        string notice = "")
    {
        var normalized = (text ?? string.Empty).ReplaceLineEndings("\n").Trim();
        if (normalized.Length == 0)
            return new(fallbackSignature, string.Empty, [], string.Empty, notice);

        var paragraphs = BlankLinePattern().Split(normalized)
            .Select(static paragraph => paragraph.Trim())
            .Where(static paragraph => paragraph.Length > 0)
            .ToList();
        if (paragraphs.Count == 0)
            return new(fallbackSignature, string.Empty, [], string.Empty, notice);

        var signature = fallbackSignature;
        var firstLines = paragraphs[0].Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (firstLines.Length > 1 && LooksLikeSignature(firstLines[0]))
        {
            signature = firstLines[0];
            paragraphs[0] = string.Join(' ', firstLines.Skip(1));
        }
        else if (LooksLikeSignature(paragraphs[0]))
        {
            signature = paragraphs[0];
            paragraphs.RemoveAt(0);
        }

        var summaryParts = new List<string>();
        var parameters = new List<DocumentationParameterPresentation>();
        var returns = string.Empty;
        foreach (var paragraph in paragraphs)
        {
            if (TryParseParameter(paragraph, out var parameter))
            {
                parameters.Add(parameter);
                continue;
            }
            if (paragraph.StartsWith('→'))
            {
                returns = paragraph[1..].Trim();
                continue;
            }
            summaryParts.Add(string.Join(' ',
                paragraph.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        }

        return new(
            signature,
            string.Join(Environment.NewLine + Environment.NewLine, summaryParts),
            parameters,
            returns,
            notice);
    }

    private static bool TryParseParameter(
        string paragraph,
        out DocumentationParameterPresentation parameter)
    {
        var isActive = paragraph.StartsWith('▶');
        if (!isActive && !paragraph.StartsWith('•'))
        {
            parameter = null!;
            return false;
        }

        var lines = paragraph.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var header = lines[0][1..].Trim();
        var separator = header.IndexOf(" : ", StringComparison.Ordinal);
        parameter = separator < 0
            ? new(header, string.Empty, string.Join(' ', lines.Skip(1)), isActive)
            : new(
                header[..separator].Trim(),
                header[(separator + 3)..].Trim(),
                string.Join(' ', lines.Skip(1)),
                isActive);
        return true;
    }

    private static bool LooksLikeSignature(string text)
    {
        var firstLine = text.Split('\n', 2, StringSplitOptions.None)[0].Trim();
        if (firstLine.Length == 0 || firstLine.EndsWith('.')) return false;
        return firstLine.Contains('(') ||
               firstLine.Contains('{') ||
               firstLine.Contains(" : ", StringComparison.Ordinal) ||
               SignaturePrefixPattern().IsMatch(firstLine);
    }

    [GeneratedRegex(@"^(?:(?:class|struct|record|interface|enum|delegate|namespace)\s+|(?:[\w.<>\[\]?]+\s+){1,3}[\w.<>]+$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SignaturePrefixPattern();

    [GeneratedRegex(@"\n\s*\n", RegexOptions.CultureInvariant)]
    private static partial Regex BlankLinePattern();
}
