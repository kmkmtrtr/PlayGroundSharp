using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PlayGroundSharp.LanguageService;

namespace PlayGroundSharp.App;

public partial class DocumentationView : UserControl
{
    private static readonly HashSet<string> Keywords =
    [
        "abstract", "async", "bool", "byte", "char", "class", "decimal", "delegate", "double",
        "dynamic", "enum", "event", "float", "in", "int", "interface", "internal", "long", "new",
        "object", "out", "override", "params", "private", "protected", "public", "readonly", "record",
        "ref", "required", "sbyte", "sealed", "short", "static", "string", "struct", "uint", "ulong",
        "ushort", "virtual", "void"
    ];

    public DocumentationView()
    {
        InitializeComponent();
        Clear();
    }

    public void ShowMessage(string? message)
    {
        var text = string.IsNullOrWhiteSpace(message) ? string.Empty : message.Trim();
        Render(DocumentationPresentation.Message(text), "i");
    }

    public void ShowCompletion(CompletionCandidate candidate, string? text, string? notice) =>
        Render(
            DocumentationPresentationParser.Parse(text, candidate.DisplayText, notice ?? string.Empty),
            candidate.KindGlyph);

    public void ShowQuickInfo(string text) =>
        Render(DocumentationPresentationParser.Parse(text), "C#");

    public void ShowSignature(SignatureInformation signature, string parameterFallback)
    {
        var parameters = signature.ActiveParameter >= 0 &&
                         signature.ActiveParameter < signature.Parameters.Count
            ? new[]
            {
                new DocumentationParameterPresentation(
                    signature.Parameters[signature.ActiveParameter].Name,
                    signature.Parameters[signature.ActiveParameter].TypeName,
                    string.IsNullOrWhiteSpace(signature.Parameters[signature.ActiveParameter].Summary)
                        ? parameterFallback
                        : signature.Parameters[signature.ActiveParameter].Summary,
                    true)
            }
            : [];
        Render(new(
            signature.DisplayText,
            signature.Summary,
            parameters,
            string.Empty,
            string.Empty), "M");
    }

    public void Clear()
    {
        SignatureText.Inlines.Clear();
        GlyphText.Text = string.Empty;
        HeaderPanel.Visibility = Visibility.Collapsed;
        HeaderDivider.Visibility = Visibility.Collapsed;
        NoticePanel.Visibility = Visibility.Collapsed;
        NoticeText.Text = string.Empty;
        SummarySection.Visibility = Visibility.Collapsed;
        SummaryText.Text = string.Empty;
        ParametersSection.Visibility = Visibility.Collapsed;
        ParameterList.Children.Clear();
        ReturnsSection.Visibility = Visibility.Collapsed;
        ReturnsText.Text = string.Empty;
    }

    private void Render(DocumentationPresentation presentation, string glyph)
    {
        Clear();
        if (!string.IsNullOrWhiteSpace(presentation.Signature))
        {
            GlyphText.Text = glyph;
            AddSignatureRuns(presentation.Signature);
            HeaderPanel.Visibility = Visibility.Visible;
            HeaderDivider.Visibility = Visibility.Visible;
        }
        if (!string.IsNullOrWhiteSpace(presentation.Notice))
        {
            NoticeText.Text = presentation.Notice;
            NoticePanel.Visibility = Visibility.Visible;
        }
        if (!string.IsNullOrWhiteSpace(presentation.Summary))
        {
            SummaryText.Text = presentation.Summary;
            SummarySection.Visibility = Visibility.Visible;
        }
        if (presentation.Parameters.Count > 0)
        {
            foreach (var parameter in presentation.Parameters)
                ParameterList.Children.Add(CreateParameterCard(parameter));
            ParametersSection.Visibility = Visibility.Visible;
        }
        if (!string.IsNullOrWhiteSpace(presentation.Returns))
        {
            ReturnsText.Text = presentation.Returns;
            ReturnsSection.Visibility = Visibility.Visible;
        }
    }

    private void AddSignatureRuns(string signature)
    {
        foreach (Match match in SignatureTokenPattern().Matches(signature))
        {
            var token = match.Value;
            var run = new Run(token);
            if (string.IsNullOrWhiteSpace(token))
            {
                SignatureText.Inlines.Add(run);
                continue;
            }

            if (Keywords.Contains(token))
                SetRunStyle(run, "AccentBrush", FontWeights.Normal);
            else if (char.IsLetter(token[0]) && IsMethodToken(signature, match))
                SetRunStyle(run, "ExplorerMethodBrush", FontWeights.SemiBold);
            else if (char.IsLetter(token[0]) && char.IsUpper(token[0]))
                SetRunStyle(run, "ExplorerClassBrush", FontWeights.Normal);
            else if (!char.IsLetterOrDigit(token[0]) && token[0] != '_')
                SetRunStyle(run, "MutedBrush", FontWeights.Normal);
            SignatureText.Inlines.Add(run);
        }
    }

    private static bool IsMethodToken(string signature, Match match)
    {
        var remainder = signature.AsSpan(match.Index + match.Length).TrimStart();
        return !remainder.IsEmpty && remainder[0] == '(';
    }

    private static void SetRunStyle(Run run, string brushKey, FontWeight weight)
    {
        run.SetResourceReference(TextElement.ForegroundProperty, brushKey);
        run.FontWeight = weight;
    }

    private static Border CreateParameterCard(DocumentationParameterPresentation parameter)
    {
        var header = new TextBlock
        {
            FontFamily = new("Cascadia Mono,Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        var name = new Run(parameter.Name) { FontWeight = FontWeights.SemiBold };
        name.SetResourceReference(TextElement.ForegroundProperty,
            parameter.IsActive ? "AccentBrush" : "ForegroundBrush");
        header.Inlines.Add(name);
        if (!string.IsNullOrWhiteSpace(parameter.TypeName))
        {
            var separator = new Run(" : ");
            separator.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
            header.Inlines.Add(separator);
            var type = new Run(parameter.TypeName);
            type.SetResourceReference(TextElement.ForegroundProperty, "ExplorerClassBrush");
            header.Inlines.Add(type);
        }

        var content = new StackPanel();
        if (parameter.IsActive)
        {
            var label = new TextBlock
            {
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new(0, 0, 0, 3)
            };
            label.SetResourceReference(TextBlock.TextProperty, "Assist.CurrentParameter");
            label.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            content.Children.Add(label);
        }
        content.Children.Add(header);
        if (!string.IsNullOrWhiteSpace(parameter.Summary))
        {
            var summary = new TextBlock
            {
                Text = parameter.Summary,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new("Segoe UI Variable Text,Segoe UI"),
                FontSize = 12,
                LineHeight = 18,
                Margin = new(0, 4, 0, 0)
            };
            summary.SetResourceReference(TextElement.ForegroundProperty, "ForegroundBrush");
            content.Children.Add(summary);
        }

        var card = new Border
        {
            Child = content,
            CornerRadius = new(4),
            Padding = new(8, 6, 8, 6),
            Margin = new(0, 0, 0, 4),
            BorderThickness = parameter.IsActive ? new(3, 0, 0, 0) : new(0)
        };
        if (parameter.IsActive)
        {
            card.SetResourceReference(BackgroundProperty, "SelectionBrush");
            card.SetResourceReference(BorderBrushProperty, "AccentBrush");
        }
        return card;
    }

    [GeneratedRegex(@"@?[\p{L}_][\p{L}\p{N}_]*|\d+|\s+|.", RegexOptions.CultureInvariant)]
    private static partial Regex SignatureTokenPattern();
}
