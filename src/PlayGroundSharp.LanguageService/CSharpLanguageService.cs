using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Host.Mef;
using PlayGroundSharp.Core;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
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
    int? ReplacementStart = null,
    string? SourceNamespace = null)
{
    public string TextToInsert => InsertionText ?? DisplayText;
    public bool IsExtensionMethod => Tags.Contains("ExtensionMethod", StringComparer.OrdinalIgnoreCase);
    public string? NamespaceHint => RequiredNamespace ?? SourceNamespace;
    public bool HasNamespaceHint => !string.IsNullOrWhiteSpace(NamespaceHint);
    public bool RequiresImport => !string.IsNullOrWhiteSpace(RequiredNamespace);
    public string NamespaceDisplayText => RequiresImport ? $"using {RequiredNamespace}" : NamespaceHint ?? string.Empty;
    public string AccessibleDisplayText => HasNamespaceHint
        ? $"{DisplayText}, {NamespaceDisplayText}"
        : DisplayText;
}
public sealed record QuickInfoResult(string Text);
public sealed record SignatureParameterInformation(string Name, string TypeName, string Summary);
public sealed record SignatureInformation(
    string DisplayText,
    string Summary,
    IReadOnlyList<SignatureParameterInformation> Parameters,
    int ActiveParameter);
public sealed record SignatureHelpResult(IReadOnlyList<SignatureInformation> Signatures, int SelectedSignature);

/// <summary>Describes one documented parameter on an explorer method.</summary>
public sealed record ExplorerParameterInfo(string Name, string TypeName, string Summary);

/// <summary>Describes a type or method shown in the session-aware symbol explorer.</summary>
public sealed record SymbolExplorerEntry(
    string Namespace,
    string Name,
    string DisplayName,
    string Kind,
    string AssemblyName,
    string? ContainingType,
    string Signature,
    string Summary,
    IReadOnlyList<ExplorerParameterInfo> Parameters,
    string Returns,
    IReadOnlyList<string> InheritedTypes)
{
    public string FullName => string.Join('.', new[] { Namespace == "(session)" ? null : Namespace, ContainingType, Name }
        .Where(static part => !string.IsNullOrEmpty(part)));
}

/// <summary>Provides Roslyn editor services over the same session inputs used by execution.</summary>
public sealed class CSharpLanguageService
{
    private static readonly Regex NumericLiteralReceiverPattern = new(
        @"(?<![\p{L}\p{N}_\.])(?<literal>[+-]?(?:0[xX][0-9A-Fa-f_]+|0[bB][01_]+|(?:\d[\d_]*)(?:\.\d[\d_]*)?(?:[eE][+-]?\d[\d_]*)?[fFdDmM]?))\.$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Lazy<MefHostServices> WorkspaceHost = new(
        static () => MefHostServices.Create(MefHostServices.DefaultAssemblies),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<IReadOnlyList<MetadataReference>> PlatformReferences = new(
        CreatePlatformReferences,
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ConcurrentDictionary<string, IReadOnlyList<ReferencedType>> ReferencedTypeCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, DocumentationInfo>> DocumentationFileCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns whether accepting a submission can add entries to the symbol explorer.</summary>
    public static bool ContainsSymbolExplorerDeclarations(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var root = CSharpSyntaxTree.ParseText(
            code,
            new CSharpParseOptions(LanguageVersion.Latest, kind: SourceCodeKind.Script)).GetRoot();
        return root.DescendantNodes().Any(static node =>
            node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or
                MethodDeclarationSyntax or LocalFunctionStatementSyntax);
    }

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
            var access = root is null ? null : FindMemberAccessAtPosition(root, absolutePosition);
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
                    .Select(CreateSymbolCompletionCandidate));
            }
        }
        if (items.Any(static item => item.IsExtensionMethod && item.SourceNamespace is null))
            await AddExtensionNamespacesAsync(
                workspaceDocument.Document, workspaceDocument.CurrentOffset, position, context.Imports,
                context.ReferencePaths, items, cancellationToken)
                .ConfigureAwait(false);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (item.IsExtensionMethod && item.RequiredNamespace is null && item.SourceNamespace is { } sourceNamespace &&
                !context.Imports.Contains(sourceNamespace, StringComparer.Ordinal))
                items[index] = item with { RequiredNamespace = sourceNamespace };
        }
        ApplyNumericLiteralReceiverEdit(currentCode, position, items);
        AddUnimportedTypeCompletions(context, currentCode, position, items, cancellationToken);
        return items
            .DistinctBy(static item => (item.TextToInsert, item.RequiredNamespace), CompletionIdentityComparer.Instance)
            .OrderBy(static item => item.RequiredNamespace is not null)
            .ThenBy(static item => item.IsExtensionMethod)
            .ThenBy(static item => item.SortText, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Finds namespaces that can unambiguously resolve extension-method invocations typed without
    /// accepting their completion item. Already bound invocations and ambiguous namespaces are ignored.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetRequiredExtensionImportsAsync(
        SessionContext context,
        string currentCode,
        CancellationToken cancellationToken = default)
    {
        if (!currentCode.Contains('.') || !currentCode.Contains('(')) return [];

        using var workspaceDocument = CreateDocument(context, currentCode);
        var root = await workspaceDocument.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await workspaceDocument.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null) return [];

        var currentStart = workspaceDocument.CurrentOffset;
        var currentEnd = currentStart + currentCode.Length;
        var unresolvedInvocations = new List<UnresolvedExtensionInvocation>();
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (invocation.SpanStart < currentStart || invocation.Span.End > currentEnd ||
                invocation.Expression is not MemberAccessExpressionSyntax access)
                continue;

            var invocationSymbol = model.GetSymbolInfo(invocation, cancellationToken);
            if (invocationSymbol.Symbol is not null || invocationSymbol.CandidateSymbols.Length > 0) continue;

            var receiverSymbol = model.GetSymbolInfo(access.Expression, cancellationToken).Symbol;
            if (receiverSymbol is INamedTypeSymbol) continue;
            var receiverType = model.GetTypeInfo(access.Expression, cancellationToken).Type ?? receiverSymbol switch
            {
                ILocalSymbol local => local.Type,
                IFieldSymbol field => field.Type,
                IPropertySymbol property => property.Type,
                _ => null
            };
            if (receiverType is null or { TypeKind: TypeKind.Error or TypeKind.Dynamic }) continue;

            unresolvedInvocations.Add(new(
                invocation,
                access.Name.Identifier.ValueText,
                receiverType,
                new HashSet<string>(StringComparer.Ordinal)));
        }
        if (unresolvedInvocations.Count == 0) return [];

        var unresolvedNames = unresolvedInvocations
            .Select(static invocation => invocation.MethodName)
            .ToHashSet(StringComparer.Ordinal);
        var dynamicPaths = context.ReferencePaths
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in model.Compilation.References.OfType<PortableExecutableReference>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reference.FilePath is null || !dynamicPaths.Contains(Path.GetFullPath(reference.FilePath))) continue;
            if (model.Compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly) continue;

            foreach (var type in EnumeratePublicTypes(assembly.GlobalNamespace))
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (!method.IsExtensionMethod || !unresolvedNames.Contains(method.Name)) continue;
                var sourceNamespace = method.ContainingNamespace?.ToDisplayString();
                if (string.IsNullOrWhiteSpace(sourceNamespace) ||
                    context.Imports.Contains(sourceNamespace, StringComparer.Ordinal))
                    continue;

                foreach (var unresolved in unresolvedInvocations.Where(invocation =>
                             invocation.MethodName.Equals(method.Name, StringComparison.Ordinal)))
                {
                    var reducedMethod = method.ReduceExtensionMethod(unresolved.ReceiverType);
                    if (reducedMethod is null ||
                        !CanAcceptArgumentCount(reducedMethod, unresolved.Invocation.ArgumentList.Arguments.Count) ||
                        !GetOverloadScore(
                            reducedMethod, unresolved.Invocation.ArgumentList, model, cancellationToken).IsCompatible)
                        continue;
                    unresolved.Namespaces.Add(sourceNamespace);
                }
            }
        }

        return unresolvedInvocations
            .Where(static invocation => invocation.Namespaces.Count == 1)
            .Select(static invocation => invocation.Namespaces.Single())
            .Distinct(StringComparer.Ordinal)
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
        var item = candidate.RequiredNamespace is null || candidate.IsExtensionMethod
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

        var completionStart = candidate.ReplacementStart ?? position;
        while (candidate.ReplacementStart is null && completionStart > 0 && IsIdentifierPart(currentCode[completionStart - 1]))
            completionStart--;
        completionStart = Math.Clamp(completionStart, 0, position);
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
        var invocation = FindInvocationAtPosition(root, absolutePosition);
        if (invocation is null) return null;
        var methods = model.GetMemberGroup(invocation.Expression, cancellationToken)
            .OfType<IMethodSymbol>()
            .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
            .ToArray();
        if (methods.Length == 0) return null;

        var resolvedMethod = model.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
        var activeArgument = GetActiveArgumentIndex(invocation.ArgumentList, absolutePosition);
        var rankedMethods = methods
            .Select((method, originalIndex) => new
            {
                Method = method,
                OriginalIndex = originalIndex,
                IsResolved = AreSameMethod(method, resolvedMethod),
                Score = GetOverloadScore(method, invocation.ArgumentList, model, cancellationToken)
            })
            .OrderByDescending(static item => item.IsResolved)
            .ThenByDescending(static item => item.Score.IsCompatible)
            .ThenByDescending(static item => item.Score.IdentityConversions)
            .ThenByDescending(static item => item.Score.ImplicitConversions)
            .ThenBy(static item => item.Score.ParameterCountDistance)
            .ThenBy(static item => item.OriginalIndex)
            .Select(static item => item.Method)
            .ToArray();
        var signatures = rankedMethods.Select(method =>
        {
            var documentation = GetDocumentation(method, cancellationToken);
            var parameters = method.Parameters.Select(parameter => new SignatureParameterInformation(
                parameter.Name,
                parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                documentation.Parameters.GetValueOrDefault(parameter.Name) ?? string.Empty)).ToArray();
            return new SignatureInformation(
                method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                documentation.Summary,
                parameters,
                GetActiveParameterIndex(method, invocation.ArgumentList, activeArgument));
        }).DistinctBy(static item => item.DisplayText, StringComparer.Ordinal).ToArray();
        return new(signatures, 0);
    }

    private static InvocationExpressionSyntax? FindInvocationAtPosition(SyntaxNode root, int position)
    {
        var probePosition = Math.Clamp(
            position > root.FullSpan.Start ? position - 1 : position,
            root.FullSpan.Start,
            Math.Max(root.FullSpan.Start, root.FullSpan.End - 1));
        var invocation = root.FindToken(probePosition, findInsideTrivia: true).Parent?
            .AncestorsAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(candidate => IsPositionInArgumentList(candidate.ArgumentList, position));
        if (invocation is not null) return invocation;

        return root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(candidate => IsPositionInArgumentList(candidate.ArgumentList, position))
            .OrderBy(static candidate => candidate.ArgumentList.FullSpan.Length)
            .FirstOrDefault();

    }

    private static bool IsPositionInArgumentList(ArgumentListSyntax argumentList, int position) =>
        position >= argumentList.OpenParenToken.Span.End &&
        position <= (argumentList.CloseParenToken.IsMissing
            ? argumentList.FullSpan.End
            : argumentList.CloseParenToken.SpanStart);

    private static int GetActiveArgumentIndex(ArgumentListSyntax argumentList, int position) =>
        argumentList.Arguments.GetSeparators().Count(separator => separator.Span.End <= position);

    private static int GetActiveParameterIndex(
        IMethodSymbol method,
        ArgumentListSyntax argumentList,
        int activeArgument)
    {
        if (method.Parameters.Length == 0) return -1;
        var argument = activeArgument < argumentList.Arguments.Count
            ? argumentList.Arguments[activeArgument]
            : null;
        if (argument?.NameColon?.Name.Identifier.ValueText is { Length: > 0 } name)
        {
            return FindParameterIndex(method, name);
        }
        if (activeArgument < method.Parameters.Length) return activeArgument;
        return method.Parameters[^1].IsParams ? method.Parameters.Length - 1 : -1;
    }

    private static bool AreSameMethod(IMethodSymbol method, IMethodSymbol? resolvedMethod) =>
        resolvedMethod is not null && SymbolEqualityComparer.Default.Equals(
            method.OriginalDefinition, resolvedMethod.OriginalDefinition);

    private static OverloadScore GetOverloadScore(
        IMethodSymbol method,
        ArgumentListSyntax argumentList,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var identityConversions = 0;
        var implicitConversions = 0;
        for (var argumentIndex = 0; argumentIndex < argumentList.Arguments.Count; argumentIndex++)
        {
            var argument = argumentList.Arguments[argumentIndex];
            var parameterIndex = GetParameterIndex(method, argument, argumentIndex);
            if (parameterIndex < 0 || !HasMatchingRefKind(method.Parameters[parameterIndex], argument))
                return OverloadScore.Incompatible;
            if (argument.Expression.IsMissing) continue;

            var sourceType = model.GetTypeInfo(argument.Expression, cancellationToken).Type;
            if (sourceType is null or { TypeKind: TypeKind.Error or TypeKind.Dynamic }) continue;
            var parameter = method.Parameters[parameterIndex];
            var targetTypes = GetConversionTargetTypes(parameter, argumentIndex);
            var conversions = targetTypes
                .Select(target => model.Compilation.ClassifyConversion(sourceType, target))
                .ToArray();
            if (conversions.Any(static conversion => conversion.IsIdentity))
            {
                identityConversions++;
                continue;
            }
            if (conversions.Any(static conversion => conversion.IsImplicit))
            {
                implicitConversions++;
                continue;
            }
            if (targetTypes.Any(static target => target.TypeKind == TypeKind.TypeParameter)) continue;
            return OverloadScore.Incompatible;
        }

        return new(
            true,
            identityConversions,
            implicitConversions,
            Math.Abs(method.Parameters.Length - argumentList.Arguments.Count));
    }

    private static int GetParameterIndex(IMethodSymbol method, ArgumentSyntax argument, int argumentIndex)
    {
        if (argument.NameColon?.Name.Identifier.ValueText is { Length: > 0 } name)
            return FindParameterIndex(method, name);
        if (argumentIndex < method.Parameters.Length) return argumentIndex;
        return method.Parameters is [.., { IsParams: true }] ? method.Parameters.Length - 1 : -1;
    }

    private static bool CanAcceptArgumentCount(IMethodSymbol method, int argumentCount)
    {
        var requiredCount = method.Parameters.Count(static parameter => !parameter.IsOptional && !parameter.IsParams);
        return argumentCount >= requiredCount &&
               (method.Parameters is [.., { IsParams: true }] || argumentCount <= method.Parameters.Length);
    }

    private static int FindParameterIndex(IMethodSymbol method, string name)
    {
        for (var index = 0; index < method.Parameters.Length; index++)
            if (method.Parameters[index].Name == name)
                return index;
        return -1;
    }

    private static IEnumerable<ITypeSymbol> GetConversionTargetTypes(IParameterSymbol parameter, int argumentIndex)
    {
        yield return parameter.Type;
        if (parameter.IsParams && parameter.Type is IArrayTypeSymbol arrayType &&
            argumentIndex >= parameter.Ordinal)
            yield return arrayType.ElementType;
    }

    private static bool HasMatchingRefKind(IParameterSymbol parameter, ArgumentSyntax argument)
    {
        var argumentRefKind = argument.RefKindKeyword.Kind() switch
        {
            SyntaxKind.RefKeyword => RefKind.Ref,
            SyntaxKind.OutKeyword => RefKind.Out,
            SyntaxKind.InKeyword => RefKind.In,
            _ => RefKind.None
        };
        return parameter.RefKind == argumentRefKind ||
               parameter.RefKind == RefKind.In && argumentRefKind == RefKind.None;
    }

    private readonly record struct OverloadScore(
        bool IsCompatible,
        int IdentityConversions,
        int ImplicitConversions,
        int ParameterCountDistance)
    {
        public static OverloadScore Incompatible { get; } = new(false, 0, 0, int.MaxValue);
    }

    private sealed record UnresolvedExtensionInvocation(
        InvocationExpressionSyntax Invocation,
        string MethodName,
        ITypeSymbol ReceiverType,
        HashSet<string> Namespaces);

    public async Task<IReadOnlyList<DiagnosticInfo>> GetDiagnosticsAsync(
        SessionContext context,
        string currentCode,
        CancellationToken cancellationToken = default)
    {
        using var workspaceDocument = CreateDocument(context, currentCode);
        var model = await workspaceDocument.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (model is null) return [];
        var currentStart = workspaceDocument.CurrentOffset;
        var significantEnd = currentStart + currentCode.TrimEnd().Length;
        return model.GetDiagnostics(new TextSpan(currentStart, currentCode.Length), cancellationToken)
            .Where(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Hidden)
            .Where(diagnostic => diagnostic.Id != "CS1002" || diagnostic.Location.SourceSpan.Start < significantEnd)
            .Select(diagnostic => ToDiagnostic(diagnostic, currentStart))
            .ToArray();
    }

    /// <summary>Builds the documented type and member inventory available to the current session.</summary>
    public async Task<IReadOnlyList<SymbolExplorerEntry>> GetSymbolExplorerAsync(
        SessionContext context,
        CancellationToken cancellationToken = default)
    {
        using var workspaceDocument = CreateDocument(context, string.Empty);
        var semanticModel = await workspaceDocument.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (semanticModel is null) return [];
        var compilation = semanticModel.Compilation;
        var entries = new List<SymbolExplorerEntry>();

        foreach (var import in context.Imports.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var @namespace = FindNamespace(compilation.GlobalNamespace, import);
            if (@namespace is null) continue;
            foreach (var type in @namespace.GetTypeMembers()
                         .Where(static type => type.DeclaredAccessibility == Accessibility.Public))
                AddTypeAndMethods(entries, type, import, cancellationToken);
        }

        var root = await workspaceDocument.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is not null)
        {
            var sessionTypes = root.DescendantNodes()
                .Where(static node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
                .Select(node => semanticModel.GetDeclaredSymbol(node, cancellationToken))
                .OfType<INamedTypeSymbol>()
                .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var type in sessionTypes)
                AddTypeAndMethods(entries, type, "(session)", cancellationToken);

            var sessionMethods = root.DescendantNodes()
                .Where(static node => node is MethodDeclarationSyntax or LocalFunctionStatementSyntax)
                .Where(static node => !node.Ancestors().Any(static ancestor => ancestor is BaseTypeDeclarationSyntax))
                .Select(node => semanticModel.GetDeclaredSymbol(node, cancellationToken))
                .OfType<IMethodSymbol>()
                .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default);
            foreach (var method in sessionMethods)
                entries.Add(CreateMethodEntry(method, "(session)", null, cancellationToken));
        }

        var dynamicPaths = context.ReferencePaths
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in compilation.References.OfType<PortableExecutableReference>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reference.FilePath is null || !dynamicPaths.Contains(Path.GetFullPath(reference.FilePath))) continue;
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly) continue;
            string? documentationPath = Path.ChangeExtension(reference.FilePath, ".xml");
            if (!File.Exists(documentationPath)) documentationPath = null;
            foreach (var type in EnumeratePublicTypes(assembly.GlobalNamespace))
                AddTypeAndMethods(entries, type, type.ContainingNamespace.ToDisplayString(), cancellationToken, documentationPath);
        }

        return entries
            .Where(static entry => entry.Name.Length > 0 && entry.Namespace.Length > 0)
            .GroupBy(static entry =>
                (entry.Namespace, entry.ContainingType, entry.DisplayName, entry.Kind, entry.AssemblyName))
            .Select(static group => group
                .OrderByDescending(static entry => !string.IsNullOrWhiteSpace(entry.Summary))
                .ThenByDescending(static entry => entry.Parameters.Count(static parameter => !string.IsNullOrWhiteSpace(parameter.Summary)))
                .First())
            .OrderBy(static entry => entry.Namespace, StringComparer.Ordinal)
            .ThenBy(static entry => entry.ContainingType ?? entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.ContainingType is null && entry.Kind != "method" ? 0 : 1)
            .ThenBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
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
        const string globals = "dynamic Last = default!; dynamic Out = default!; " +
            "var Data = new PlayGroundSharp.Core.LargeDataAccess();";
        var prefix = usingDirectives + Environment.NewLine + Environment.NewLine + globals + Environment.NewLine + Environment.NewLine +
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
        return paths
            .Where(path => SessionContext.DefaultReferenceAssemblyNames.Contains(
                Path.GetFileNameWithoutExtension(path)))
            .Select(path =>
        {
            var assemblyName = Path.GetFileNameWithoutExtension(path);
            var documentationPath = documentationPaths.GetValueOrDefault(assemblyName);
            if (documentationPath is null && assemblyName == "System.Private.CoreLib")
                documentationPath = documentationPaths.GetValueOrDefault("System.Runtime");
            return CreateMetadataReference(path, documentationPath);
        }).Append(CreateMetadataReference(typeof(SessionContext).Assembly.Location))
            .DistinctBy(static reference => reference.Display, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private static void ApplyNumericLiteralReceiverEdit(
        string currentCode,
        int position,
        List<CompletionCandidate> items)
    {
        var memberStart = Math.Clamp(position, 0, currentCode.Length);
        while (memberStart > 0 && SyntaxFacts.IsIdentifierPartCharacter(currentCode[memberStart - 1])) memberStart--;
        var match = NumericLiteralReceiverPattern.Match(currentCode[..memberStart]);
        if (!match.Success) return;
        var literal = match.Groups["literal"].Value;
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            items[index] = item with
            {
                InsertionText = $"({literal}).{item.TextToInsert}",
                ReplacementStart = match.Index
            };
        }
    }

    private static async Task AddExtensionNamespacesAsync(
        RoslynDocument document,
        int currentOffset,
        int position,
        IReadOnlyList<string> imports,
        IReadOnlyList<string> referencePaths,
        List<CompletionCandidate> items,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null) return;
        var absolutePosition = currentOffset + position;
        var access = FindMemberAccessAtPosition(root, absolutePosition);
        var receiverType = access is null ? null : model.GetTypeInfo(access.Expression, cancellationToken).Type;
        if (receiverType is null || access is null) return;

        var namespaceCandidates = model.LookupSymbols(
                absolutePosition, receiverType, includeReducedExtensionMethods: true)
            .Concat(model.LookupSymbols(absolutePosition, includeReducedExtensionMethods: true))
            .OfType<IMethodSymbol>()
            .Where(method => method.MethodKind == MethodKind.ReducedExtension ||
                             method.IsExtensionMethod && method.ReduceExtensionMethod(receiverType) is not null)
            .Select(method => (method.Name,
                Namespace: (method.ReducedFrom ?? method).ContainingNamespace?.ToDisplayString()))
            .Where(static item => !string.IsNullOrWhiteSpace(item.Namespace))
            .Select(static item => (item.Name, Namespace: item.Namespace!))
            .ToList();

        var unresolvedNames = items.Where(static item => item.IsExtensionMethod)
            .Select(static item => item.DisplayText)
            .ToHashSet(StringComparer.Ordinal);
        var dynamicPaths = referencePaths.Where(File.Exists).Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in model.Compilation.References.OfType<PortableExecutableReference>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reference.FilePath is null || !dynamicPaths.Contains(Path.GetFullPath(reference.FilePath))) continue;
            if (model.Compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly) continue;
            foreach (var type in EnumeratePublicTypes(assembly.GlobalNamespace))
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (!method.IsExtensionMethod || !unresolvedNames.Contains(method.Name) ||
                    method.ReduceExtensionMethod(receiverType) is null) continue;
                var @namespace = method.ContainingNamespace?.ToDisplayString();
                if (!string.IsNullOrWhiteSpace(@namespace)) namespaceCandidates.Add((method.Name, @namespace));
            }
        }

        var namespacesByName = namespaceCandidates
            .GroupBy(static item => item.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key,
                group => group.Select(static item => item.Namespace!)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(@namespace => !imports.Contains(@namespace, StringComparer.Ordinal))
                    .ThenBy(static @namespace => @namespace, StringComparer.Ordinal)
                    .First(),
                StringComparer.Ordinal);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (!namespacesByName.TryGetValue(item.DisplayText, out var sourceNamespace)) continue;
            items[index] = item with
            {
                Tags = item.IsExtensionMethod ? item.Tags : [.. item.Tags, "ExtensionMethod"],
                SourceNamespace = sourceNamespace
            };
        }
    }

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

    private static MemberAccessExpressionSyntax? FindMemberAccessAtPosition(SyntaxNode root, int position) =>
        root.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(node => node.OperatorToken.Span.End <= position &&
                           node.SpanStart <= position && node.Span.End >= position)
            .OrderBy(static node => node.Span.Length)
            .FirstOrDefault();

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
                candidate.Name,
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
            var assemblyName = reader.IsAssembly
                ? reader.GetString(reader.GetAssemblyDefinition().Name)
                : Path.GetFileNameWithoutExtension(path);
            var types = new List<ReferencedType>();
            foreach (var handle in reader.TypeDefinitions)
            {
                var definition = reader.GetTypeDefinition(handle);
                var visibility = definition.Attributes & TypeAttributes.VisibilityMask;
                if (visibility is not TypeAttributes.Public and not TypeAttributes.NestedPublic) continue;
                var metadataName = reader.GetString(definition.Name);
                var aritySeparator = metadataName.IndexOf('`');
                var name = aritySeparator < 0 ? metadataName : metadataName[..aritySeparator];
                var @namespace = reader.GetString(definition.Namespace);
                if (@namespace.Length == 0 || !SyntaxFacts.IsValidIdentifier(name)) continue;
                var kind = (definition.Attributes & TypeAttributes.ClassSemanticsMask) == TypeAttributes.Interface
                    ? "interface"
                    : "type";
                types.Add(new(name, @namespace, kind, assemblyName));
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

    private static INamespaceSymbol? FindNamespace(INamespaceSymbol root, string fullName)
    {
        var current = root;
        foreach (var segment in fullName.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.GetNamespaceMembers().FirstOrDefault(candidate => candidate.Name == segment);
            if (current is null) return null;
        }
        return current;
    }

    private static IEnumerable<INamedTypeSymbol> EnumeratePublicTypes(INamespaceSymbol @namespace)
    {
        foreach (var type in @namespace.GetTypeMembers().Where(static type =>
                     type.DeclaredAccessibility == Accessibility.Public))
        {
            yield return type;
            foreach (var nested in EnumeratePublicNestedTypes(type)) yield return nested;
        }
        foreach (var child in @namespace.GetNamespaceMembers())
            foreach (var type in EnumeratePublicTypes(child))
                yield return type;
    }

    private static IEnumerable<INamedTypeSymbol> EnumeratePublicNestedTypes(INamedTypeSymbol containingType)
    {
        foreach (var type in containingType.GetTypeMembers().Where(static type =>
                     type.DeclaredAccessibility == Accessibility.Public))
        {
            yield return type;
            foreach (var nested in EnumeratePublicNestedTypes(type)) yield return nested;
        }
    }

    private static void AddTypeAndMethods(
        ICollection<SymbolExplorerEntry> entries,
        INamedTypeSymbol type,
        string namespaceName,
        CancellationToken cancellationToken,
        string? documentationPath = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var typeName = GetTypeDisplayName(type);
        var documentation = GetDocumentation(type, cancellationToken, documentationPath);
        var assemblyName = namespaceName == "(session)" ? "Session" : type.ContainingAssembly?.Name ?? string.Empty;
        entries.Add(new(
            namespaceName,
            typeName,
            typeName,
            GetTypeKind(type),
            assemblyName,
            null,
            type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            documentation.Summary,
            [],
            string.Empty,
            GetInheritedTypes(type)));

        foreach (var method in type.GetMembers().OfType<IMethodSymbol>()
                     .Where(static method =>
                         method.DeclaredAccessibility == Accessibility.Public &&
                         !method.IsImplicitlyDeclared &&
                         method.MethodKind is MethodKind.Ordinary or MethodKind.Constructor))
            entries.Add(CreateMethodEntry(method, namespaceName, typeName, cancellationToken, documentationPath));

        if (type.TypeKind == TypeKind.Enum)
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>()
                         .Where(static field =>
                             field.DeclaredAccessibility == Accessibility.Public &&
                             field.HasConstantValue &&
                             !field.IsImplicitlyDeclared))
                entries.Add(CreateEnumMemberEntry(
                    field, namespaceName, typeName, cancellationToken, documentationPath));
    }

    private static SymbolExplorerEntry CreateEnumMemberEntry(
        IFieldSymbol field,
        string namespaceName,
        string containingType,
        CancellationToken cancellationToken,
        string? documentationPath)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var documentation = GetDocumentation(field, cancellationToken, documentationPath);
        var assemblyName = namespaceName == "(session)" ? "Session" : field.ContainingAssembly?.Name ?? string.Empty;
        var constantValue = Convert.ToString(field.ConstantValue, CultureInfo.InvariantCulture) ?? string.Empty;
        return new(
            namespaceName,
            field.Name,
            $"{field.Name} = {constantValue}",
            "enum member",
            assemblyName,
            containingType,
            $"{containingType}.{field.Name} = {constantValue}",
            documentation.Summary,
            [],
            string.Empty,
            []);
    }

    private static SymbolExplorerEntry CreateMethodEntry(
        IMethodSymbol method,
        string namespaceName,
        string? containingType,
        CancellationToken cancellationToken,
        string? documentationPath = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var documentation = GetDocumentation(method, cancellationToken, documentationPath);
        var displayName = GetMethodDisplayName(method);
        var assemblyName = namespaceName == "(session)" ? "Session" : method.ContainingAssembly?.Name ?? string.Empty;
        var parameters = method.Parameters.Select(parameter => new ExplorerParameterInfo(
            parameter.Name,
            parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            documentation.Parameters.GetValueOrDefault(parameter.Name) ?? string.Empty)).ToArray();
        return new(
            namespaceName,
            method.Name,
            displayName,
            method.MethodKind == MethodKind.Constructor ? "constructor" : "method",
            assemblyName,
            containingType,
            method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            documentation.Summary,
            parameters,
            documentation.Returns,
            []);
    }

    private static IReadOnlyList<string> GetInheritedTypes(INamedTypeSymbol type)
    {
        var inheritedTypes = new List<string>();
        if (type.TypeKind == TypeKind.Class && type.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
            inheritedTypes.Add(baseType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        inheritedTypes.AddRange(type.Interfaces.Select(static @interface =>
            @interface.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        return inheritedTypes.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string GetMethodDisplayName(IMethodSymbol method)
    {
        var name = method.MethodKind == MethodKind.Constructor ? method.ContainingType.Name : method.Name;
        if (method.TypeParameters.Length > 0)
            name += $"<{string.Join(", ", method.TypeParameters.Select(static parameter => parameter.Name))}>";
        var parameters = string.Join(", ", method.Parameters.Select(static parameter =>
            $"{GetRefKindPrefix(parameter.RefKind)}{parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {parameter.Name}"));
        return $"{name}({parameters})";
    }

    private static string GetRefKindPrefix(RefKind refKind) => refKind switch
    {
        RefKind.Ref => "ref ",
        RefKind.Out => "out ",
        RefKind.In => "in ",
        _ => string.Empty
    };

    private static string GetTypeDisplayName(INamedTypeSymbol type) => type.TypeParameters.Length == 0
        ? type.Name
        : $"{type.Name}<{string.Join(", ", type.TypeParameters.Select(static parameter => parameter.Name))}>";

    private static string GetTypeKind(INamedTypeSymbol type) => type.TypeKind switch
    {
        TypeKind.Interface => "interface",
        TypeKind.Struct when type.IsRecord => "record struct",
        TypeKind.Struct => "struct",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        TypeKind.Class when type.IsRecord => "record",
        TypeKind.Class => "class",
        _ => "type"
    };

    private static DocumentationInfo GetDocumentation(
        ISymbol symbol,
        CancellationToken cancellationToken,
        string? documentationPath = null)
    {
        var xml = symbol.GetDocumentationCommentXml(expandIncludes: true, cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(xml) && documentationPath is not null &&
            symbol.GetDocumentationCommentId() is { } documentationId)
        {
            var documentation = DocumentationFileCache.GetOrAdd(documentationPath, ReadDocumentationFile);
            if (documentation.TryGetValue(documentationId, out var matched)) return matched;
        }
        if (string.IsNullOrWhiteSpace(xml)) xml = GetSourceDocumentationXml(symbol, cancellationToken);
        if (string.IsNullOrWhiteSpace(xml)) return DocumentationInfo.Empty;
        try
        {
            return ParseDocumentationElement(XElement.Parse(xml));
        }
        catch (System.Xml.XmlException)
        {
            return DocumentationInfo.Empty;
        }
    }

    private static IReadOnlyDictionary<string, DocumentationInfo> ReadDocumentationFile(string path)
    {
        try
        {
            return XDocument.Load(path).Descendants("member")
                .Where(static element => element.Attribute("name")?.Value is { Length: > 0 })
                .ToDictionary(
                    static element => element.Attribute("name")!.Value,
                    ParseDocumentationElement,
                    StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return new Dictionary<string, DocumentationInfo>(StringComparer.Ordinal);
        }
    }

    private static DocumentationInfo ParseDocumentationElement(XElement root)
    {
        var parameters = root.Elements("param")
            .Where(static element => element.Attribute("name")?.Value is { Length: > 0 })
            .ToDictionary(
                static element => element.Attribute("name")!.Value,
                GetDocumentationText,
                StringComparer.Ordinal);
        return new(
            GetDocumentationText(root.Element("summary")),
            parameters,
            GetDocumentationText(root.Element("returns")));
    }

    private static string? GetSourceDocumentationXml(ISymbol symbol, CancellationToken cancellationToken)
    {
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var syntax = reference.GetSyntax(cancellationToken);
            var documentation = syntax.GetLeadingTrivia()
                .Select(static trivia => trivia.GetStructure())
                .OfType<DocumentationCommentTriviaSyntax>()
                .FirstOrDefault();
            if (documentation is null) continue;
            var content = string.Concat(documentation.ToFullString()
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(static line =>
                {
                    var trimmed = line.TrimStart();
                    if (!trimmed.StartsWith("///", StringComparison.Ordinal)) return trimmed;
                    trimmed = trimmed[3..];
                    return trimmed.StartsWith(' ') ? trimmed[1..] : trimmed;
                }));
            return $"<member>{content}</member>";
        }
        return null;
    }

    private static string GetDocumentationSummary(ISymbol symbol, CancellationToken cancellationToken) =>
        GetDocumentation(symbol, cancellationToken).Summary;

    private static string GetDocumentationText(XElement? element)
    {
        if (element is null) return string.Empty;
        var copy = new XElement(element);
        foreach (var see in copy.Descendants("see").ToArray())
        {
            var reference = see.Attribute("cref")?.Value;
            if (reference is { Length: > 2 } && reference[1] == ':') reference = reference[2..];
            see.ReplaceWith(reference ?? see.Attribute("langword")?.Value ?? see.Value);
        }
        foreach (var parameter in copy.Descendants("paramref").ToArray())
            parameter.ReplaceWith(parameter.Attribute("name")?.Value ?? parameter.Value);
        return string.Join(' ', copy.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
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

    private static CompletionCandidate CreateSymbolCompletionCandidate(ISymbol symbol)
    {
        var extensionMethod = symbol as IMethodSymbol;
        var tag = extensionMethod is not null &&
                  (extensionMethod.IsExtensionMethod || extensionMethod.MethodKind == MethodKind.ReducedExtension)
            ? "ExtensionMethod"
            : symbol.Kind.ToString();
        var sourceNamespace = extensionMethod is null
            ? null
            : (extensionMethod.ReducedFrom ?? extensionMethod).ContainingNamespace?.ToDisplayString();
        return new(symbol.Name, symbol.Name, symbol.Name, [tag], SourceNamespace: sourceNamespace);
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

    private sealed record DocumentationInfo(
        string Summary,
        IReadOnlyDictionary<string, string> Parameters,
        string Returns)
    {
        public static DocumentationInfo Empty { get; } = new(
            string.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal),
            string.Empty);
    }

    private sealed record ReferencedType(string Name, string Namespace, string Kind, string AssemblyName);
}
