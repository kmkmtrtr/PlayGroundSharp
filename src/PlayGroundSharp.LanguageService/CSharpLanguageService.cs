using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Host.Mef;
using PlayGroundSharp.Core;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using RoslynDocument = Microsoft.CodeAnalysis.Document;

namespace PlayGroundSharp.LanguageService;

public sealed record CompletionCandidate(
    string DisplayText,
    string FilterText,
    string SortText,
    IReadOnlyList<string> Tags,
    string? InsertionText = null,
    string? RequiredNamespace = null,
    int? ReplacementStart = null)
{
    public string TextToInsert => InsertionText ?? DisplayText;
}
public sealed record QuickInfoResult(string Text);
public sealed record SignatureInformation(string DisplayText, string Summary);
public sealed record SignatureHelpResult(IReadOnlyList<SignatureInformation> Signatures, int ActiveParameter);

/// <summary>Provides Roslyn editor services over the same session inputs used by execution.</summary>
public sealed class CSharpLanguageService
{
    private static readonly Lazy<MefHostServices> WorkspaceHost = new(
        static () => MefHostServices.Create(MefHostServices.DefaultAssemblies),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<IReadOnlyList<MetadataReference>> PlatformReferences = new(
        CreatePlatformReferences,
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ConcurrentDictionary<string, IReadOnlyList<ReferencedType>> ReferencedTypeCache =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<CompletionCandidate>> GetCompletionsAsync(
        SessionContext context,
        string currentCode,
        int position,
        CancellationToken cancellationToken = default)
    {
        var analysisCode = GetCompletionAnalysisCode(currentCode, position);
        using var workspaceDocument = CreateDocument(context, analysisCode);
        var service = CompletionService.GetService(workspaceDocument.Document)
            ?? throw new InvalidOperationException("C# completion service is unavailable.");
        var completions = await service.GetCompletionsAsync(workspaceDocument.Document,
            workspaceDocument.CurrentOffset + position, cancellationToken: cancellationToken).ConfigureAwait(false);
        var items = completions?.ItemsList.Select(static item => new CompletionCandidate(
            item.DisplayText, item.FilterText, item.SortText, item.Tags)).ToList() ?? [];
        if (items.Count == 0)
        {
            var root = await workspaceDocument.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var model = await workspaceDocument.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var absolutePosition = workspaceDocument.CurrentOffset + position;
            var access = root?.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                .LastOrDefault(node => node.OperatorToken.SpanStart == absolutePosition - 1);
            var receiverType = access is null ? null : model?.GetTypeInfo(access.Expression, cancellationToken).Type;
            if (receiverType is null && access is not null && model is not null)
            {
                receiverType = model.GetSymbolInfo(access.Expression, cancellationToken).Symbol switch
                {
                    ILocalSymbol local => local.Type,
                    IFieldSymbol field => field.Type,
                    IPropertySymbol property => property.Type,
                    _ => null
                };
            }
            if (receiverType is not null && model is not null)
            {
                var memberSymbols = GetInstanceMembers(receiverType, model.Compilation)
                    .Concat(model.LookupSymbols(absolutePosition, receiverType, includeReducedExtensionMethods: true))
                    .Concat(model.LookupSymbols(absolutePosition, includeReducedExtensionMethods: true)
                        .OfType<IMethodSymbol>()
                        .Where(method => method.IsExtensionMethod && method.ReduceExtensionMethod(receiverType) is not null));
                items.AddRange(memberSymbols
                    .Where(static symbol => symbol.CanBeReferencedByName)
                    .Select(static symbol => new CompletionCandidate(symbol.Name, symbol.Name, symbol.Name, [symbol.Kind.ToString()])));
            }
        }
        AddUnimportedTypeCompletions(context, currentCode, position, items, cancellationToken);
        return items
            .DistinctBy(static item => (item.TextToInsert, item.RequiredNamespace), CompletionIdentityComparer.Instance)
            .OrderBy(static item => item.RequiredNamespace is not null)
            .ThenBy(static item => item.SortText, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<string?> GetCompletionDescriptionAsync(
        SessionContext context,
        string currentCode,
        int position,
        CompletionCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        var analysisCode = GetCompletionAnalysisCode(currentCode, position);
        using var workspaceDocument = CreateDocument(context, analysisCode);
        var service = CompletionService.GetService(workspaceDocument.Document);
        if (service is null) return null;
        var completions = await service.GetCompletionsAsync(workspaceDocument.Document,
            workspaceDocument.CurrentOffset + position, cancellationToken: cancellationToken).ConfigureAwait(false);
        var item = candidate.RequiredNamespace is null
            ? completions?.ItemsList.FirstOrDefault(item =>
                item.DisplayText == candidate.DisplayText && item.FilterText == candidate.FilterText)
            : null;
        var description = item is null
            ? null
            : await service.GetDescriptionAsync(
                workspaceDocument.Document, item, cancellationToken).ConfigureAwait(false);
        var completionText = description is null
            ? string.Empty
            : string.Concat(description.TaggedParts.Select(static part => part.Text)).Trim();

        var completionStart = position;
        while (completionStart > 0 && IsIdentifierPart(currentCode[completionStart - 1])) completionStart--;
        var completedCode = currentCode.Remove(completionStart, position - completionStart)
            .Insert(completionStart, candidate.TextToInsert);
        var symbolPosition = completionStart + Math.Max(0, candidate.TextToInsert.Length - 1);
        var summaryContext = candidate.RequiredNamespace is null
            ? context
            : context with { Imports = [.. context.Imports, candidate.RequiredNamespace] };
        var summary = await GetSymbolDocumentationSummaryAsync(
            summaryContext, completedCode, symbolPosition, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(summary) && !completionText.Contains(summary, StringComparison.Ordinal))
            return completionText.Length == 0 ? summary : completionText + Environment.NewLine + summary;
        return completionText.Length == 0 ? null : completionText;
    }

    public async Task<QuickInfoResult?> GetQuickInfoAsync(
        SessionContext context,
        string currentCode,
        int position,
        CancellationToken cancellationToken = default)
    {
        using var workspaceDocument = CreateDocument(context, currentCode);
        var service = QuickInfoService.GetService(workspaceDocument.Document);
        if (service is null) return null;
        var item = await service.GetQuickInfoAsync(workspaceDocument.Document,
            workspaceDocument.CurrentOffset + position, cancellationToken).ConfigureAwait(false);
        if (item is null) return null;
        return new(string.Join(Environment.NewLine,
            item.Sections.Select(section => string.Concat(section.TaggedParts.Select(static part => part.Text)))));
    }

    public async Task<SignatureHelpResult?> GetSignatureHelpAsync(
        SessionContext context,
        string currentCode,
        int position,
        CancellationToken cancellationToken = default)
    {
        using var workspaceDocument = CreateDocument(context, currentCode);
        var root = await workspaceDocument.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await workspaceDocument.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null) return null;
        var absolutePosition = workspaceDocument.CurrentOffset + position;
        var token = root.FindToken(Math.Max(0, Math.Min(absolutePosition, root.FullSpan.End - 1)));
        var invocation = token.Parent?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
        if (invocation is null) return null;
        var symbols = model.GetMemberGroup(invocation.Expression, cancellationToken)
            .OfType<IMethodSymbol>()
            .Select(method => new SignatureInformation(
                method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                GetDocumentationSummary(method, cancellationToken)))
            .DistinctBy(static item => item.DisplayText, StringComparer.Ordinal)
            .ToArray();
        if (symbols.Length == 0) return null;
        var activeParameter = invocation.ArgumentList.Arguments.Count;
        if (invocation.ArgumentList.Arguments.LastOrDefault()?.Span.Contains(absolutePosition) == true)
            activeParameter = Math.Max(0, activeParameter - 1);
        return new(symbols, activeParameter);
    }

    public async Task<IReadOnlyList<DiagnosticInfo>> GetDiagnosticsAsync(
        SessionContext context,
        string currentCode,
        CancellationToken cancellationToken = default)
    {
        using var workspaceDocument = CreateDocument(context, currentCode);
        var model = await workspaceDocument.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (model is null) return [];
        var currentStart = workspaceDocument.CurrentOffset;
        return model.GetDiagnostics(new TextSpan(currentStart, currentCode.Length), cancellationToken)
            .Where(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Hidden)
            .Select(diagnostic => ToDiagnostic(diagnostic, currentStart))
            .ToArray();
    }

    private static WorkspaceDocument CreateDocument(SessionContext context, string currentCode)
    {
        var workspace = new AdhocWorkspace(WorkspaceHost.Value);
        var projectId = ProjectId.CreateNewId();
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest, kind: SourceCodeKind.Script);
        var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            usings: context.Imports, nullableContextOptions: NullableContextOptions.Enable);
        var references = PlatformReferences.Value
            .Concat(context.ReferencePaths.Where(File.Exists).Select(CreateMetadataReference))
            .GroupBy(static reference => reference.Display, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First());
        var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Create(), "PlayGroundSharp.Session",
            "PlayGroundSharp.Session", LanguageNames.CSharp, parseOptions: parseOptions,
            compilationOptions: compilationOptions, metadataReferences: references);
        workspace.AddProject(projectInfo);
        var usingDirectives = string.Join(Environment.NewLine, context.Imports.Select(static import => $"using {import};"));
        var prefix = usingDirectives + Environment.NewLine + Environment.NewLine +
            string.Join(Environment.NewLine + Environment.NewLine, context.Submissions.Select(NormalizeHistoricalSubmission));
        if (prefix.Length > 0) prefix += Environment.NewLine + Environment.NewLine;
        var document = workspace.AddDocument(projectId, "Current.csx", SourceText.From(prefix + currentCode));
        return new(workspace, document, prefix.Length);
    }

    private static IReadOnlyList<MetadataReference> CreatePlatformReferences()
    {
        var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];
        var documentationPaths = GetReferenceDocumentationPaths();
        return paths.Select(path => CreateMetadataReference(path,
            documentationPaths.GetValueOrDefault(Path.GetFileNameWithoutExtension(path)))).ToArray();
    }

    private static MetadataReference CreateMetadataReference(string path) =>
        CreateMetadataReference(path, File.Exists(Path.ChangeExtension(path, ".xml"))
            ? Path.ChangeExtension(path, ".xml")
            : null);

    private static MetadataReference CreateMetadataReference(string path, string? documentationPath) =>
        MetadataReference.CreateFromFile(path, documentation: documentationPath is null
            ? DocumentationProvider.Default
            : XmlDocumentationProvider.CreateFromFile(documentationPath));

    private static IReadOnlyDictionary<string, string> GetReferenceDocumentationPaths()
    {
        var runtimeDirectory = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        var dotnetRoot = runtimeDirectory.Parent?.Parent?.Parent;
        var packRoot = dotnetRoot is null
            ? null
            : Path.Combine(dotnetRoot.FullName, "packs", "Microsoft.NETCore.App.Ref");
        if (packRoot is null || !Directory.Exists(packRoot)) return new Dictionary<string, string>();

        var referenceDirectory = Directory.EnumerateDirectories(packRoot)
            .Select(path => (Path: path, Version: Version.TryParse(Path.GetFileName(path), out var version) ? version : null))
            .Where(item => item.Version?.Major == Environment.Version.Major)
            .OrderByDescending(static item => item.Version)
            .Select(item => Path.Combine(item.Path, "ref", $"net{Environment.Version.Major}.0"))
            .FirstOrDefault(Directory.Exists);
        if (referenceDirectory is null) return new Dictionary<string, string>();
        return Directory.EnumerateFiles(referenceDirectory, "*.xml")
            .ToDictionary(static path => Path.GetFileNameWithoutExtension(path)!, StringComparer.OrdinalIgnoreCase);
    }

    private static string GetCompletionAnalysisCode(string currentCode, int position) =>
        position > 0 && position <= currentCode.Length && currentCode[position - 1] == '.'
            ? currentCode.Insert(position, "__PlayGroundSharpCompletion")
            : currentCode;

    private static string NormalizeHistoricalSubmission(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.Latest, kind: SourceCodeKind.Script));
        var root = tree.GetCompilationUnitRoot();
        var insertionPosition = root.EndOfFileToken.GetPreviousToken().Span.End;
        var memberCompletion = CompleteMemberDeclaration(code, insertionPosition);
        if (memberCompletion is not null) return memberCompletion;

        var trailingToken = root.EndOfFileToken.GetPreviousToken(includeZeroWidth: true);
        return trailingToken.IsMissing && trailingToken.IsKind(SyntaxKind.SemicolonToken)
            ? code.Insert(trailingToken.SpanStart, ";")
            : code;
    }

    private static string? CompleteMemberDeclaration(string code, int insertionPosition)
    {
        var candidate = code.Insert(insertionPosition, ";");
        var member = SyntaxFactory.ParseMemberDeclaration(candidate,
            options: new CSharpParseOptions(LanguageVersion.Latest));
        return member is not null && !member.ContainsDiagnostics &&
            member.GetLastToken().IsKind(SyntaxKind.SemicolonToken)
            ? candidate
            : null;
    }

    private static bool IsIdentifierPart(char character) => char.IsLetterOrDigit(character) || character == '_';

    private static void AddUnimportedTypeCompletions(
        SessionContext context,
        string currentCode,
        int position,
        ICollection<CompletionCandidate> items,
        CancellationToken cancellationToken)
    {
        var start = Math.Clamp(position, 0, currentCode.Length);
        while (start > 0 && IsIdentifierPart(currentCode[start - 1])) start--;
        var prefix = currentCode[start..Math.Clamp(position, start, currentCode.Length)];
        if (prefix.Length < 2 || start > 0 && currentCode[start - 1] == '.') return;

        var imported = context.Imports.ToHashSet(StringComparer.Ordinal);
        var candidates = context.ReferencePaths
            .Where(File.Exists)
            .SelectMany(path => ReferencedTypeCache.GetOrAdd(Path.GetFullPath(path), ReadReferencedTypes))
            .Where(type => type.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !imported.Contains(type.Namespace))
            .DistinctBy(static type => (type.Name, type.Namespace))
            .OrderBy(static type => type.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static type => type.Namespace, StringComparer.Ordinal)
            .Take(100);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var roslynDuplicates = items.Where(item =>
                item.RequiredNamespace is null &&
                item.FilterText.Equals(candidate.Name, StringComparison.Ordinal) &&
                item.SortText.Contains(candidate.Namespace, StringComparison.Ordinal)).ToArray();
            foreach (var duplicate in roslynDuplicates) items.Remove(duplicate);
            items.Add(new(
                $"{candidate.Name}  ({candidate.Namespace})",
                candidate.Name,
                candidate.Name,
                ["Type", "Import"],
                candidate.Name,
                candidate.Namespace));
        }
    }

    private static IReadOnlyList<ReferencedType> ReadReferencedTypes(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata) return [];
            var reader = peReader.GetMetadataReader();
            var types = new List<ReferencedType>();
            foreach (var handle in reader.TypeDefinitions)
            {
                var definition = reader.GetTypeDefinition(handle);
                var visibility = definition.Attributes & TypeAttributes.VisibilityMask;
                if (visibility is not TypeAttributes.Public and not TypeAttributes.NestedPublic) continue;
                var name = reader.GetString(definition.Name);
                var @namespace = reader.GetString(definition.Namespace);
                if (@namespace.Length == 0 || name.Contains('`') || !SyntaxFacts.IsValidIdentifier(name)) continue;
                types.Add(new(name, @namespace));
            }
            return types;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return [];
        }
    }

    public IReadOnlyList<string> GetReferenceNamespaces(SessionContext context) => context.ReferencePaths
        .Where(File.Exists)
        .SelectMany(path => ReferencedTypeCache.GetOrAdd(Path.GetFullPath(path), ReadReferencedTypes))
        .Select(static type => type.Namespace)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string GetDocumentationSummary(ISymbol symbol, CancellationToken cancellationToken)
    {
        var xml = symbol.GetDocumentationCommentXml(expandIncludes: true, cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(xml)) return string.Empty;
        try
        {
            var summary = XElement.Parse(xml).Element("summary")?.Value;
            return summary is null
                ? string.Empty
                : string.Join(' ', summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        catch (System.Xml.XmlException)
        {
            return string.Empty;
        }
    }

    private static async Task<string?> GetSymbolDocumentationSummaryAsync(
        SessionContext context,
        string currentCode,
        int position,
        CancellationToken cancellationToken)
    {
        using var workspaceDocument = CreateDocument(context, currentCode);
        var root = await workspaceDocument.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await workspaceDocument.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null) return null;
        var absolutePosition = workspaceDocument.CurrentOffset + Math.Clamp(position, 0, currentCode.Length);
        var token = root.FindToken(Math.Min(absolutePosition, root.FullSpan.End - 1));
        var node = token.Parent?.AncestorsAndSelf().FirstOrDefault(static item => item is SimpleNameSyntax);
        if (node is null) return null;
        var symbolInfo = model.GetSymbolInfo(node, cancellationToken);
        return symbolInfo.Symbol is { } symbol
            ? GetDocumentationSummary(symbol, cancellationToken)
            : symbolInfo.CandidateSymbols
                .Select(symbol => GetDocumentationSummary(symbol, cancellationToken))
                .FirstOrDefault(static summary => !string.IsNullOrWhiteSpace(summary));
    }

    private static IEnumerable<ISymbol> GetInstanceMembers(ITypeSymbol type, Compilation compilation)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers()) yield return member;
        }
        if (type is IArrayTypeSymbol)
        {
            foreach (var member in compilation.GetSpecialType(SpecialType.System_Array).GetMembers()) yield return member;
        }
        foreach (var @interface in type.AllInterfaces)
        {
            foreach (var member in @interface.GetMembers()) yield return member;
        }
    }

    private static DiagnosticInfo ToDiagnostic(Diagnostic diagnostic, int offset)
    {
        var span = diagnostic.Location.GetLineSpan().Span;
        return new(diagnostic.Id, diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => DiagnosticLevel.Error,
            DiagnosticSeverity.Warning => DiagnosticLevel.Warning,
            _ => DiagnosticLevel.Info
        }, diagnostic.GetMessage(), span.Start.Line + 1, Math.Max(1, span.Start.Character + 1 - offset),
            span.End.Line + 1, Math.Max(1, span.End.Character + 1 - offset));
    }

    private sealed class WorkspaceDocument(AdhocWorkspace workspace, RoslynDocument document, int currentOffset) : IDisposable
    {
        public RoslynDocument Document { get; } = document;
        public int CurrentOffset { get; } = currentOffset;
        public void Dispose() => workspace.Dispose();
    }

    private sealed class CompletionIdentityComparer : IEqualityComparer<(string Text, string? Namespace)>
    {
        public static CompletionIdentityComparer Instance { get; } = new();
        public bool Equals((string Text, string? Namespace) left, (string Text, string? Namespace) right) =>
            left.Text.Equals(right.Text, StringComparison.Ordinal) &&
            string.Equals(left.Namespace, right.Namespace, StringComparison.Ordinal);
        public int GetHashCode((string Text, string? Namespace) value) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(value.Text),
                value.Namespace is null ? 0 : StringComparer.Ordinal.GetHashCode(value.Namespace));
    }

    private sealed record ReferencedType(string Name, string Namespace);
}
