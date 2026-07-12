using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Host.Mef;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.LanguageService;

public sealed record CompletionCandidate(string DisplayText, string FilterText, string SortText, IReadOnlyList<string> Tags);
public sealed record QuickInfoResult(string Text);
public sealed record SignatureHelpResult(IReadOnlyList<string> Signatures, int ActiveParameter);

/// <summary>Provides Roslyn editor services over the same session inputs used by execution.</summary>
public sealed class CSharpLanguageService
{
    public async Task<IReadOnlyList<CompletionCandidate>> GetCompletionsAsync(
        SessionContext context,
        string currentCode,
        int position,
        CancellationToken cancellationToken = default)
    {
        var analysisCode = position > 0 && position <= currentCode.Length && currentCode[position - 1] == '.'
            ? currentCode.Insert(position, "__PlayGroundSharpCompletion")
            : currentCode;
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
        return items.DistinctBy(static item => item.DisplayText, StringComparer.Ordinal).ToArray();
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
            .Select(static method => method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
            .Distinct(StringComparer.Ordinal)
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
        var workspace = new AdhocWorkspace(MefHostServices.Create(MefHostServices.DefaultAssemblies));
        var projectId = ProjectId.CreateNewId();
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest, kind: SourceCodeKind.Script);
        var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            usings: context.Imports, nullableContextOptions: NullableContextOptions.Enable);
        var references = GetPlatformReferences()
            .Concat(context.ReferencePaths.Where(File.Exists).Select(static path => MetadataReference.CreateFromFile(path)))
            .GroupBy(static reference => reference.Display, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First());
        var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Create(), "PlayGroundSharp.Session",
            "PlayGroundSharp.Session", LanguageNames.CSharp, parseOptions: parseOptions,
            compilationOptions: compilationOptions, metadataReferences: references);
        workspace.AddProject(projectInfo);
        var usingDirectives = string.Join(Environment.NewLine, context.Imports.Select(static import => $"using {import};"));
        var prefix = usingDirectives + Environment.NewLine + Environment.NewLine +
            string.Join(Environment.NewLine + Environment.NewLine, context.Submissions);
        if (prefix.Length > 0) prefix += Environment.NewLine + Environment.NewLine;
        var document = workspace.AddDocument(projectId, "Current.csx", SourceText.From(prefix + currentCode));
        return new(workspace, document, prefix.Length);
    }

    private static IEnumerable<MetadataReference> GetPlatformReferences()
    {
        var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];
        return paths.Select(static path => MetadataReference.CreateFromFile(path));
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

    private sealed class WorkspaceDocument(AdhocWorkspace workspace, Document document, int currentOffset) : IDisposable
    {
        public Document Document { get; } = document;
        public int CurrentOffset { get; } = currentOffset;
        public void Dispose() => workspace.Dispose();
    }
}
