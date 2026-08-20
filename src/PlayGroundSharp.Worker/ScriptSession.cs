using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Scripting;
using PlayGroundSharp.Core;
using System.Text.Json;

namespace PlayGroundSharp.Worker;

public sealed record ScriptExecutionResult(
    bool StateAccepted,
    bool HasReturnValue,
    object? ReturnValue,
    ResultSnapshot? Snapshot,
    IReadOnlyList<DiagnosticInfo> Diagnostics,
    ExceptionInfo? Exception,
    int TotalDiagnosticCount = 0);

public sealed record ScriptInspectionResult(
    ResultSnapshot? Snapshot,
    IReadOnlyList<DiagnosticInfo> Diagnostics,
    ExceptionInfo? Exception,
    int TotalDiagnosticCount = 0);

/// <summary>Owns the mutable Roslyn ScriptState for one Worker session.</summary>
public sealed class ScriptSession
{
    private const int MaximumTransferredDiagnostics = 100;
    private const int MaximumVariableSnapshotNodes = 512;
    private const int MaximumVariableSnapshotTextCharacters = 256 * 1024;
    private const int MaximumVariableSnapshotTotalNodes = 50_000;
    private const int MaximumVariableSnapshotTotalTextCharacters = 10 * 1024 * 1024;
    private readonly object globals;
    private readonly Type globalsType;
    private readonly ResultHistory resultHistory;
    private readonly ResultSnapshotFactory snapshots = new();
    private readonly AsyncSequenceEvaluator asyncSequences;
    private readonly List<string> submissions = [];
    private readonly List<string> imports = [.. SessionContext.DefaultImports];
    private readonly List<string> references = [];
    private readonly Dictionary<string, (string Identity, string Path)> assemblyIdentities = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> resolvedAssemblyNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool requiresReferenceAssemblyBootstrap;
    private ScriptState<object?>? state;
    private ScriptOptions options;

    public ScriptSession(
        IReadOnlyList<string>? platformReferencePaths = null,
        string? targetFramework = null)
    {
        asyncSequences = new(snapshots);
        requiresReferenceAssemblyBootstrap = platformReferencePaths is { Count: > 0 };
        if (platformReferencePaths is { Count: > 0 })
        {
            targetFramework ??= InferTargetFramework(platformReferencePaths);
            var generated = TargetFrameworkGlobalsFactory.Create(targetFramework, platformReferencePaths);
            globals = generated.Instance;
            globalsType = generated.Type;
            resultHistory = new();
            SetGlobal(nameof(SessionGlobals.Out), resultHistory);
            SetGlobal(nameof(SessionGlobals.Data), new LargeDataAccess());
            var runtimeSystemReference = Path.Combine(
                Path.GetDirectoryName(typeof(object).Assembly.Location)!,
                "System.Runtime.dll");
            options = ScriptOptions.Default
                .WithImports(imports)
                .WithReferences(platformReferencePaths
                    .Where(path => File.Exists(path) &&
                                   !Path.GetFileName(path).Equals(
                                       "System.Runtime.dll",
                                       StringComparison.OrdinalIgnoreCase))
                    .Select(static path => MetadataReference.CreateFromFile(path))
                    .Append(MetadataReference.CreateFromFile(runtimeSystemReference))
                    .Append(MetadataReference.CreateFromFile(generated.AssemblyPath)));
        }
        else
        {
            var typedGlobals = new SessionGlobals();
            globals = typedGlobals;
            globalsType = typeof(SessionGlobals);
            resultHistory = typedGlobals.Out;
            options = ScriptOptions.Default
                .WithImports(imports)
                .AddReferences(
                    typeof(object).Assembly,
                    typeof(Enumerable).Assembly,
                    typeof(SessionGlobals).Assembly,
                    typeof(JsonElement).Assembly,
                    typeof(Microsoft.CSharp.RuntimeBinder.Binder).Assembly,
                    typeof(System.Numerics.BigInteger).Assembly);
        }
        foreach (var reference in options.MetadataReferences)
        {
            var display = reference.Display;
            if (string.IsNullOrWhiteSpace(display)) continue;
            var separator = display.LastIndexOf(": ", StringComparison.Ordinal);
            var normalizedDisplay = separator >= 0 ? display[(separator + 2)..] : display;
            resolvedAssemblyNames.Add(Path.GetFileNameWithoutExtension(normalizedDisplay));
        }
    }

    public SessionContext Context => new([.. submissions], [.. imports], [.. references]);

    public IReadOnlyList<VariableInfo> GetVariables(CancellationToken cancellationToken = default)
    {
        if (state is null) return [];
        var variables = new List<VariableInfo>();
        var remainingNodes = MaximumVariableSnapshotTotalNodes;
        var remainingTextCharacters = MaximumVariableSnapshotTotalTextCharacters;
        foreach (var variable in state.Variables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var typeName = variable.Type.FullName ?? variable.Type.Name;
            ResultSnapshot snapshot;
            if (remainingNodes <= 0 || remainingTextCharacters <= 0)
            {
                snapshot = new(
                    SnapshotKind.MaxDepth,
                    "… variable snapshot limit reached",
                    typeName,
                    IsTruncated: true);
            }
            else
            {
                snapshot = CreateVariableSnapshot(
                    variable,
                    Math.Min(MaximumVariableSnapshotNodes, remainingNodes),
                    Math.Min(MaximumVariableSnapshotTextCharacters, remainingTextCharacters),
                    cancellationToken);
                var usage = MeasureSnapshot(snapshot);
                remainingNodes -= usage.Nodes;
                remainingTextCharacters -= usage.TextCharacters;
            }
            variables.Add(new(variable.Name, typeName, snapshot, variable.IsReadOnly));
        }
        return variables;
    }

    public IReadOnlyList<RetainedResultInfo> GetRetainedResults(CancellationToken cancellationToken = default)
    {
        var namedReferences = state?.Variables
            .Select(static variable => variable.Value)
            .Where(static value => value is not null && !value.GetType().IsValueType)
            .ToArray() ?? [];
        var results = new List<RetainedResultInfo>();
        var remainingNodes = MaximumVariableSnapshotTotalNodes;
        var remainingTextCharacters = MaximumVariableSnapshotTotalTextCharacters;
        foreach (var result in resultHistory.UnnamedResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Value is not null &&
                namedReferences.Any(value => ReferenceEquals(value, result.Value)))
                continue;

            ResultSnapshot snapshot;
            if (remainingNodes <= 0 || remainingTextCharacters <= 0)
            {
                snapshot = new(
                    SnapshotKind.MaxDepth,
                    "… retained result snapshot limit reached",
                    result.Value?.GetType().FullName ?? result.TypeExpression,
                    IsTruncated: true);
            }
            else
            {
                try
                {
                    snapshot = snapshots.Create(
                        result.Value,
                        Math.Min(MaximumVariableSnapshotNodes, remainingNodes),
                        Math.Min(MaximumVariableSnapshotTextCharacters, remainingTextCharacters),
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    snapshot = new(
                        SnapshotKind.Exception,
                        $"Snapshot failed: {error.GetType().Name}: {error.Message}",
                        result.Value?.GetType().FullName ?? result.TypeExpression);
                }
                var usage = MeasureSnapshot(snapshot);
                remainingNodes -= usage.Nodes;
                remainingTextCharacters -= usage.TextCharacters;
            }

            results.Add(new(
                result.SubmissionIndex,
                snapshot.TypeName ?? result.Value?.GetType().FullName ?? result.TypeExpression,
                result.TypeExpression,
                snapshot));
        }
        return results;
    }

    public async Task<ScriptExecutionResult> ExecuteAsync(
        int submissionIndex,
        string code,
        Action<string>? standardOutput = null,
        Action<string>? standardError = null,
        CancellationToken cancellationToken = default,
        Action<int, ResultSnapshot>? streamedResult = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var outputWriter = new BoundedEventWriter(standardOutput);
        using var errorWriter = new BoundedEventWriter(standardError);
        Console.SetOut(outputWriter);
        Console.SetError(errorWriter);
        SetGlobal(nameof(SessionGlobals.ExecutionCancellation), cancellationToken);
        try
        {
            var executableCode = PrepareSubmission(code);
            ScriptState<object?> candidate;
            try
            {
                if (state is null && requiresReferenceAssemblyBootstrap)
                {
                    // Roslyn scripting needs one successfully loaded submission before a failed
                    // compilation when reference assemblies target an older runtime. Keep this
                    // bootstrap invisible to session history and variables.
                    state = await CSharpScript.Create<object?>(string.Empty, options, globalsType)
                        .RunAsync(globals, static _ => true, cancellationToken).ConfigureAwait(false);
                }
                candidate = state is null
                    ? await CSharpScript.Create<object?>(executableCode, options, globalsType)
                        .RunAsync(globals, static _ => true, cancellationToken).ConfigureAwait(false)
                    : await state.ContinueWithAsync<object?>(executableCode, options, static _ => true, cancellationToken).ConfigureAwait(false);
            }
            catch (CompilationErrorException error)
            {
                return new(
                    false,
                    false,
                    null,
                    null,
                    error.Diagnostics.Take(MaximumTransferredDiagnostics).Select(ToDiagnostic).ToArray(),
                    null,
                    error.Diagnostics.Length);
            }

            if (candidate.Exception is OperationCanceledException cancelled && cancellationToken.IsCancellationRequested)
                throw cancelled;
            if (candidate.Exception is not null)
            {
                state = candidate;
                submissions.Add(code);
                return new(true, false, null, null, [], ResultSnapshotFactory.CreateException(candidate.Exception));
            }

            var compilation = candidate.Script.GetCompilation();
            var hasReturnValue = HasTrailingValueExpression(compilation);
            var resultType = hasReturnValue ? GetTrailingResultType(compilation) : null;
            var resultTypeExpression = hasReturnValue
                ? GetTrailingResultTypeExpression(compilation, candidate.ReturnValue)
                : null;
            ResultSnapshot? snapshot = null;
            if (hasReturnValue)
            {
                try
                {
                    var streamedResultType = TupleElementNameMapper.GetAsyncSequenceResultType(resultType);
                    var streamed = streamedResult is not null && candidate.ReturnValue is not null &&
                                   await asyncSequences.TryEvaluateAsync(
                                       candidate.ReturnValue,
                                       (sourceIndex, streamedSnapshot) => streamedResult(
                                           sourceIndex,
                                           TupleElementNameMapper.Apply(streamedSnapshot, streamedResultType)),
                                       cancellationToken).ConfigureAwait(false);
                    if (!streamed)
                        snapshot = TupleElementNameMapper.Apply(
                            snapshots.Create(candidate.ReturnValue, cancellationToken),
                            resultType);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    snapshot = new(
                        SnapshotKind.Exception,
                        $"Snapshot failed: {error.GetType().Name}: {error.Message}",
                        candidate.ReturnValue?.GetType().FullName);
                }
            }

            state = candidate;
            submissions.Add(code);
            if (hasReturnValue)
            {
                SetGlobal(nameof(SessionGlobals.Last), candidate.ReturnValue);
                var trailingIdentifier = GetTrailingIdentifier(candidate.Script.GetCompilation());
                resultHistory.Set(
                    submissionIndex,
                    candidate.ReturnValue,
                    resultTypeExpression ?? "dynamic",
                    trailingIdentifier is not null &&
                    candidate.Variables.Any(variable => variable.Name == trailingIdentifier));
            }

            return new(true, hasReturnValue, candidate.ReturnValue, snapshot, [], null);
        }
        finally
        {
            outputWriter.Flush();
            errorWriter.Flush();
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            SetGlobal(nameof(SessionGlobals.ExecutionCancellation), default(CancellationToken));
        }
    }

    public async Task<ScriptInspectionResult> InspectExpressionAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var outputWriter = new BoundedEventWriter(null);
        using var errorWriter = new BoundedEventWriter(null);
        Console.SetOut(outputWriter);
        Console.SetError(errorWriter);
        SetGlobal(nameof(SessionGlobals.ExecutionCancellation), cancellationToken);
        try
        {
            var executableCode = PrepareSubmission(code);
            ScriptState<object?> candidate;
            try
            {
                if (state is null && requiresReferenceAssemblyBootstrap)
                {
                    state = await CSharpScript.Create<object?>(string.Empty, options, globalsType)
                        .RunAsync(globals, static _ => true, cancellationToken).ConfigureAwait(false);
                }
                candidate = state is null
                    ? await CSharpScript.Create<object?>(executableCode, options, globalsType)
                        .RunAsync(globals, static _ => true, cancellationToken).ConfigureAwait(false)
                    : await state.ContinueWithAsync<object?>(
                        executableCode,
                        options,
                        static _ => true,
                        cancellationToken).ConfigureAwait(false);
            }
            catch (CompilationErrorException error)
            {
                return new(
                    null,
                    error.Diagnostics.Take(MaximumTransferredDiagnostics).Select(ToDiagnostic).ToArray(),
                    null,
                    error.Diagnostics.Length);
            }

            if (candidate.Exception is OperationCanceledException cancelled && cancellationToken.IsCancellationRequested)
                throw cancelled;
            if (candidate.Exception is not null)
                return new(null, [], ResultSnapshotFactory.CreateException(candidate.Exception));
            var compilation = candidate.Script.GetCompilation();
            if (!HasTrailingValueExpression(compilation))
                return new(null, [], null);

            try
            {
                return new(
                    TupleElementNameMapper.Apply(
                        snapshots.Create(candidate.ReturnValue, cancellationToken),
                        GetTrailingResultType(compilation)),
                    [],
                    null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                return new(
                    new(
                        SnapshotKind.Exception,
                        $"Snapshot failed: {error.GetType().Name}: {error.Message}",
                        candidate.ReturnValue?.GetType().FullName),
                    [],
                    null);
            }
        }
        finally
        {
            outputWriter.Flush();
            errorWriter.Flush();
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            SetGlobal(nameof(SessionGlobals.ExecutionCancellation), default(CancellationToken));
        }
    }

    public void AddReference(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) || !string.Equals(Path.GetExtension(fullPath), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Reference path must identify an existing DLL.", nameof(path));
        }
        if (references.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }
        var assemblyName = System.Reflection.AssemblyName.GetAssemblyName(fullPath);
        var simpleName = assemblyName.Name ?? throw new BadImageFormatException("Assembly has no simple name.");
        var identity = assemblyName.FullName ?? simpleName;
        if (assemblyIdentities.TryGetValue(simpleName, out var loaded) &&
            (!loaded.Identity.Equals(identity, StringComparison.OrdinalIgnoreCase) || !loaded.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Assembly '{simpleName}' is already loaded from another path or version. Worker reconstruction is required.");
        }
        var platformReferences = requiresReferenceAssemblyBootstrap
            ? []
            : PlatformReferenceResolver.ResolveRuntimeDependencies([fullPath])
                .Where(path => resolvedAssemblyNames.Add(Path.GetFileNameWithoutExtension(path)))
                .Select(static path => MetadataReference.CreateFromFile(path))
                .ToArray();
        if (platformReferences.Length > 0) options = options.AddReferences(platformReferences);
        options = options.AddReferences(MetadataReference.CreateFromFile(fullPath));
        resolvedAssemblyNames.Add(simpleName);
        references.Add(fullPath);
        assemblyIdentities[simpleName] = (identity, fullPath);
    }

    public void AddUsing(string @namespace)
    {
        ValidateNamespace(@namespace);
        if (!imports.Contains(@namespace, StringComparer.Ordinal))
        {
            imports.Add(@namespace);
            options = options.AddImports(@namespace);
        }
    }

    public void RemoveUsing(string @namespace)
    {
        ValidateNamespace(@namespace);
        if (state is not null)
            throw new InvalidOperationException("Removing a using requires a fresh Worker session.");
        if (imports.Remove(@namespace)) options = options.WithImports(imports);
    }

    public void Reset()
    {
        state = null;
        submissions.Clear();
        SetGlobal(nameof(SessionGlobals.Last), null);
        resultHistory.Clear();
    }

    private void SetGlobal(string name, object? value)
    {
        var property = globalsType.GetProperty(name)
            ?? throw new MissingMemberException(globalsType.FullName, name);
        property.SetValue(globals, value);
    }

    private static string InferTargetFramework(IReadOnlyList<string> referencePaths)
    {
        var systemRuntime = referencePaths.FirstOrDefault(path =>
            Path.GetFileName(path).Equals("System.Runtime.dll", StringComparison.OrdinalIgnoreCase));
        var major = systemRuntime is null
            ? Environment.Version.Major
            : System.Reflection.AssemblyName.GetAssemblyName(systemRuntime).Version?.Major ??
              Environment.Version.Major;
        return $"net{major}.0";
    }

    private ResultSnapshot CreateVariableSnapshot(
        ScriptVariable variable,
        int maximumNodes,
        int maximumTextCharacters,
        CancellationToken cancellationToken)
    {
        try
        {
            return snapshots.Create(
                variable.Value,
                maximumNodes,
                maximumTextCharacters,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new(SnapshotKind.Exception, $"{error.GetType().Name}: {error.Message}", variable.Type.FullName);
        }
    }

    private static (int Nodes, int TextCharacters) MeasureSnapshot(ResultSnapshot snapshot)
    {
        var nodes = 1;
        var textCharacters = (snapshot.Display?.Length ?? 0) + (snapshot.TypeName?.Length ?? 0);
        if (snapshot.Properties is not null)
        {
            foreach (var property in snapshot.Properties)
            {
                var child = MeasureSnapshot(property.Value);
                nodes += child.Nodes;
                textCharacters += property.Name.Length + child.TextCharacters;
            }
        }
        if (snapshot.Items is not null)
        {
            foreach (var item in snapshot.Items)
            {
                var child = MeasureSnapshot(item);
                nodes += child.Nodes;
                textCharacters += child.TextCharacters;
            }
        }
        return (nodes, textCharacters);
    }

    private static void ValidateNamespace(string @namespace)
    {
        if (string.IsNullOrWhiteSpace(@namespace) ||
            @namespace.Split('.').Any(static segment => !SyntaxFacts.IsValidIdentifier(segment)))
        {
            throw new ArgumentException("Namespace is invalid.", nameof(@namespace));
        }
    }

    private static string PrepareSubmission(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.Latest, kind: SourceCodeKind.Script));
        var root = tree.GetCompilationUnitRoot();
        if (root.Members.LastOrDefault() is GlobalStatementSyntax
            {
                Statement: ExpressionStatementSyntax expression
            })
        {
            return expression.SemicolonToken.IsMissing
                ? code
                : code.Remove(expression.SemicolonToken.SpanStart, expression.SemicolonToken.Span.Length);
        }

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

    private static bool HasTrailingValueExpression(Compilation compilation)
    {
        var tree = compilation.SyntaxTrees.Last();
        var root = tree.GetCompilationUnitRoot();
        if (root.Members.LastOrDefault() is not GlobalStatementSyntax
        {
            Statement: ExpressionStatementSyntax expression
        })
        {
            return false;
        }

        var type = compilation.GetSemanticModel(tree).GetTypeInfo(expression.Expression).Type;
        return type?.SpecialType != SpecialType.System_Void;
    }

    private static string GetTrailingResultTypeExpression(Compilation compilation, object? value)
    {
        var tree = compilation.SyntaxTrees.Last();
        var root = tree.GetCompilationUnitRoot();
        if (root.Members.LastOrDefault() is not GlobalStatementSyntax
            {
                Statement: ExpressionStatementSyntax expression
            })
            return "dynamic";

        var type = compilation.GetSemanticModel(tree).GetTypeInfo(expression.Expression).ConvertedType;
        if (type is null || type.TypeKind is TypeKind.Dynamic or TypeKind.Error || ContainsAnonymousType(type))
            return TryFormatRuntimeType(value?.GetType()) ?? "dynamic";

        var format = SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
        return type.ToDisplayString(format);
    }

    private static ITypeSymbol? GetTrailingResultType(Compilation compilation)
    {
        var tree = compilation.SyntaxTrees.Last();
        var root = tree.GetCompilationUnitRoot();
        if (root.Members.LastOrDefault() is not GlobalStatementSyntax
            {
                Statement: ExpressionStatementSyntax expression
            })
            return null;

        return compilation.GetSemanticModel(tree).GetTypeInfo(expression.Expression).ConvertedType;
    }

    private static string? GetTrailingIdentifier(Compilation compilation)
    {
        var root = compilation.SyntaxTrees.Last().GetCompilationUnitRoot();
        if (root.Members.LastOrDefault() is not GlobalStatementSyntax
            {
                Statement: ExpressionStatementSyntax expression
            })
            return null;
        ExpressionSyntax value = expression.Expression;
        while (value is ParenthesizedExpressionSyntax parenthesized)
            value = parenthesized.Expression;
        return value is IdentifierNameSyntax identifier ? identifier.Identifier.ValueText : null;
    }

    private static string? TryFormatRuntimeType(Type? type)
    {
        if (type is not null && typeof(System.Text.Json.Nodes.JsonValue).IsAssignableFrom(type))
            return "global::System.Text.Json.Nodes.JsonValue";
        if (type is null || type.IsGenericParameter || type.IsByRef || type.IsPointer || !type.IsVisible ||
            type.Name.StartsWith('<') ||
            type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false) &&
            type.Name.Contains("AnonymousType", StringComparison.Ordinal))
            return null;
        if (type.IsArray)
        {
            var element = TryFormatRuntimeType(type.GetElementType());
            if (element is null) return null;
            return $"{element}[{new string(',', type.GetArrayRank() - 1)}]";
        }
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition.IsNested || definition.FullName is not { } fullName) return null;
            var marker = fullName.IndexOf('`');
            if (marker < 0) return null;
            var arguments = type.GetGenericArguments().Select(TryFormatRuntimeType).ToArray();
            if (arguments.Any(static argument => argument is null)) return null;
            return $"global::{fullName[..marker]}<{string.Join(", ", arguments!)}>";
        }
        return type.FullName is { } name ? $"global::{name.Replace('+', '.')}" : null;
    }

    private static bool ContainsAnonymousType(ITypeSymbol type) => type switch
    {
        INamedTypeSymbol { IsAnonymousType: true } => true,
        INamedTypeSymbol named => named.TypeArguments.Any(ContainsAnonymousType),
        IArrayTypeSymbol array => ContainsAnonymousType(array.ElementType),
        IPointerTypeSymbol pointer => ContainsAnonymousType(pointer.PointedAtType),
        _ => false
    };

    private static DiagnosticInfo ToDiagnostic(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan().Span;
        return new(diagnostic.Id, diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => DiagnosticLevel.Error,
            DiagnosticSeverity.Warning => DiagnosticLevel.Warning,
            _ => DiagnosticLevel.Info
        }, diagnostic.GetMessage(), span.Start.Line + 1, span.Start.Character + 1, span.End.Line + 1, span.End.Character + 1);
    }
}
