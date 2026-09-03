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
    private const int MaximumVariableSnapshotTextCharacters = 128 * 1024;
    private const int MaximumVariableSnapshotTotalNodes = 50_000;
    private const int MaximumVariableSnapshotTotalTextCharacters = 2 * 1024 * 1024;
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
    private readonly Dictionary<string, VariableStaticType> variableStaticTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ITypeSymbol> retainedResultStaticTypes = [];
    private ITypeSymbol? lastResultStaticType;
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
                    typeof(HttpClient).Assembly,
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
            if (File.Exists(normalizedDisplay)) RegisterAssemblyIdentity(normalizedDisplay);
        }
    }

    public SessionContext Context => new([.. submissions], [.. imports], [.. references]);

    public IReadOnlyList<VariableInfo> GetVariables(CancellationToken cancellationToken = default)
    {
        if (state is null) return [];
        var sessionVariables = state.Variables.ToArray();
        var typeNames = new string[sessionVariables.Length];
        var variableSnapshots = new ResultSnapshot[sessionVariables.Length];
        var snapshotUsages = new (int Nodes, int TextCharacters)[sessionVariables.Length];
        var remainingNodes = MaximumVariableSnapshotTotalNodes;
        var remainingTextCharacters = MaximumVariableSnapshotTotalTextCharacters;
        for (var variableIndex = 0; variableIndex < sessionVariables.Length; variableIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var variable = sessionVariables[variableIndex];
            var typeName = variable.Type.FullName ?? variable.Type.Name;
            typeNames[variableIndex] = typeName;
            ResultSnapshot snapshot;
            (int Nodes, int TextCharacters) usage;
            if (remainingNodes <= 0 || remainingTextCharacters <= 0)
            {
                snapshot = new(
                    SnapshotKind.MaxDepth,
                    "… variable snapshot limit reached",
                    typeName,
                    IsTruncated: true);
                usage = MeasureSnapshot(snapshot);
            }
            else
            {
                var remainingVariableCount = sessionVariables.Length - variableIndex;
                snapshot = CreateVariableSnapshot(
                    variable,
                    Math.Max(1, remainingNodes / remainingVariableCount),
                    Math.Max(1, remainingTextCharacters / remainingVariableCount),
                    cancellationToken);
                usage = MeasureSnapshot(snapshot);
                remainingNodes -= usage.Nodes;
                remainingTextCharacters -= usage.TextCharacters;
            }
            variableSnapshots[variableIndex] = snapshot;
            snapshotUsages[variableIndex] = usage;
        }

        // The initial pass guarantees every variable a fair share. If later variables do not
        // consume theirs, let earlier truncated values use the otherwise-idle aggregate budget.
        for (var variableIndex = 0;
             variableIndex < sessionVariables.Length && (remainingNodes > 0 || remainingTextCharacters > 0);
             variableIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = variableSnapshots[variableIndex];
            if (!current.IsTruncated) continue;

            var currentUsage = snapshotUsages[variableIndex];
            var expanded = CreateVariableSnapshot(
                sessionVariables[variableIndex],
                Math.Max(1, currentUsage.Nodes + Math.Max(0, remainingNodes)),
                Math.Max(1, currentUsage.TextCharacters + Math.Max(0, remainingTextCharacters)),
                cancellationToken);
            var expandedUsage = MeasureSnapshot(expanded);
            var addedNodes = expandedUsage.Nodes - currentUsage.Nodes;
            var addedTextCharacters = expandedUsage.TextCharacters - currentUsage.TextCharacters;
            if (addedNodes > remainingNodes || addedTextCharacters > remainingTextCharacters ||
                (addedNodes <= 0 && addedTextCharacters <= 0 && expanded.IsTruncated))
                continue;

            variableSnapshots[variableIndex] = expanded;
            snapshotUsages[variableIndex] = expandedUsage;
            remainingNodes -= addedNodes;
            remainingTextCharacters -= addedTextCharacters;
        }

        return sessionVariables.Select((variable, index) =>
        {
            variableStaticTypes.TryGetValue(variable.Name, out var staticType);
            return new VariableInfo(
                variable.Name,
                typeNames[index],
                variableSnapshots[index],
                variable.IsReadOnly,
                GetCompletionTypeExpression(staticType, variableSnapshots[index]));
        }).ToArray();
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
                    if (result.PreviewSnapshot is { } preview)
                    {
                        var previewUsage = MeasureSnapshot(preview);
                        snapshot = previewUsage.Nodes <= remainingNodes &&
                                   previewUsage.TextCharacters <= remainingTextCharacters
                            ? preview
                            : new(
                                SnapshotKind.MaxDepth,
                                "… retained result snapshot limit reached",
                                result.Value?.GetType().FullName ?? result.TypeExpression,
                                IsTruncated: true);
                    }
                    else
                    {
                        snapshot = snapshots.Create(
                            result.Value,
                            Math.Min(MaximumVariableSnapshotNodes, remainingNodes),
                            Math.Min(MaximumVariableSnapshotTextCharacters, remainingTextCharacters),
                            cancellationToken);
                    }
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

            var compilation = candidate.Script.GetCompilation();
            CaptureVariableStaticTypes(compilation);
            if (candidate.Exception is OperationCanceledException cancelled && cancellationToken.IsCancellationRequested)
                throw cancelled;
            if (candidate.Exception is not null)
            {
                state = candidate;
                submissions.Add(code);
                return new(true, false, null, null, [], ResultSnapshotFactory.CreateException(candidate.Exception));
            }

            var hasReturnValue = HasTrailingValueExpression(compilation);
            var resultType = hasReturnValue ? GetEffectiveTrailingResultType(compilation) : null;
            var resultTypeExpression = hasReturnValue
                ? (resultType is null ? null : TryFormatStaticType(resultType)) ??
                  GetTrailingResultTypeExpression(compilation, candidate.ReturnValue)
                : null;
            ResultSnapshot? snapshot = null;
            ResultSnapshot? retainedPreview = null;
            if (hasReturnValue)
            {
                try
                {
                    var streamedResultType = TupleElementNameMapper.GetAsyncSequenceResultType(resultType);
                    var streamedEntries = new List<(int SourceIndex, ResultSnapshot Snapshot)>();
                    var streamed = streamedResult is not null && candidate.ReturnValue is not null &&
                                   await asyncSequences.TryEvaluateAsync(
                                       candidate.ReturnValue,
                                       (sourceIndex, streamedSnapshot) =>
                                       {
                                           var mapped = TupleElementNameMapper.Apply(
                                               streamedSnapshot,
                                               streamedResultType);
                                           streamedEntries.Add((sourceIndex, mapped));
                                           streamedResult(sourceIndex, mapped);
                                       },
                                       cancellationToken).ConfigureAwait(false);
                    if (streamed)
                        retainedPreview = CreateRetainedStreamedSnapshot(streamedEntries);
                    else
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
                    candidate.Variables.Any(variable => variable.Name == trailingIdentifier),
                    retainedPreview);
                if (resultType is not null)
                {
                    retainedResultStaticTypes[submissionIndex] = resultType;
                    lastResultStaticType = resultType;
                }
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
        bool forDataInference = false,
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
                        forDataInference
                            ? DataInferenceSnapshotFactory.Create(candidate.ReturnValue, snapshots, cancellationToken)
                            : snapshots.Create(candidate.ReturnValue, cancellationToken),
                        GetEffectiveTrailingResultType(compilation)),
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
        if (assemblyIdentities.TryGetValue(simpleName, out var loaded))
        {
            if (!loaded.Identity.Equals(identity, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Assembly '{simpleName}' is already loaded with another version. Worker reconstruction is required.");

            // The same assembly may arrive through a direct reference and again as a NuGet
            // dependency. Keep one canonical metadata path so Roslyn and the runtime cannot
            // construct distinct type identities from duplicate physical files.
            return;
        }
        var platformReferencePaths = requiresReferenceAssemblyBootstrap
            ? []
            : PlatformReferenceResolver.ResolveRuntimeDependencies([fullPath])
                .Where(path => resolvedAssemblyNames.Add(Path.GetFileNameWithoutExtension(path)))
                .ToArray();
        foreach (var dependencyPath in platformReferencePaths) RegisterAssemblyIdentity(dependencyPath);
        if (platformReferencePaths.Length > 0)
            options = options.AddReferences(platformReferencePaths.Select(static path => MetadataReference.CreateFromFile(path)));
        options = options.AddReferences(MetadataReference.CreateFromFile(fullPath));
        resolvedAssemblyNames.Add(simpleName);
        references.Add(fullPath);
        assemblyIdentities[simpleName] = (identity, fullPath);
    }

    private void RegisterAssemblyIdentity(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var assemblyName = System.Reflection.AssemblyName.GetAssemblyName(fullPath);
        var simpleName = assemblyName.Name ?? throw new BadImageFormatException("Assembly has no simple name.");
        assemblyIdentities.TryAdd(simpleName, (assemblyName.FullName ?? simpleName, fullPath));
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
        variableStaticTypes.Clear();
        retainedResultStaticTypes.Clear();
        lastResultStaticType = null;
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
            var snapshot = snapshots.Create(
                variable.Value,
                maximumNodes,
                maximumTextCharacters,
                cancellationToken);
            if (!variableStaticTypes.TryGetValue(variable.Name, out var staticType)) return snapshot;
            snapshot = TupleElementNameMapper.Apply(snapshot, staticType.Type);
            return TryFormatStaticType(staticType.Type) is { } typeExpression
                ? snapshot with { TypeExpression = typeExpression }
                : snapshot;
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

    private void CaptureVariableStaticTypes(Compilation compilation)
    {
        var tree = compilation.SyntaxTrees.Last();
        var root = tree.GetCompilationUnitRoot();
        var model = compilation.GetSemanticModel(tree);
        foreach (var field in root.Members.OfType<FieldDeclarationSyntax>())
        foreach (var declarator in field.Declaration.Variables)
        {
            var declaration = declarator.Parent as VariableDeclarationSyntax;
            var variableType = declaration is null ? null : model.GetTypeInfo(declaration.Type).Type;
            if (variableType is null) continue;
            var isImplicit = declaration!.Type.IsVar;
            var recoveredType = isImplicit
                ? TryGetBuiltInFlowStaticType(declarator.Initializer?.Value, model)
                : null;
            var allowRuntimeFallback = isImplicit &&
                                       IsBuiltInDynamicFlow(declarator.Initializer?.Value, model);
            variableStaticTypes[declarator.Identifier.ValueText] = new(
                recoveredType ?? variableType,
                allowRuntimeFallback);
        }
    }

    private ITypeSymbol? TryGetBuiltInFlowStaticType(ExpressionSyntax? expression, SemanticModel model)
    {
        if (expression is null) return null;
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;

        if (expression is ElementAccessExpressionSyntax access)
        {
            if (access.Expression is not IdentifierNameSyntax { Identifier.ValueText: "Out" } ||
                access.ArgumentList.Arguments.Count != 1 ||
                !IsSessionGlobalsProperty(access.Expression, model))
                return null;
            var constant = model.GetConstantValue(access.ArgumentList.Arguments[0].Expression);
            if (constant is { HasValue: true, Value: int index } &&
                retainedResultStaticTypes.TryGetValue(index, out var retainedType))
                return retainedType;
            return null;
        }

        if (expression is not IdentifierNameSyntax identifier) return null;
        if (identifier.Identifier.ValueText == "Last" && lastResultStaticType is not null &&
            IsSessionGlobalsProperty(identifier, model))
            return lastResultStaticType;
        if (variableStaticTypes.TryGetValue(identifier.Identifier.ValueText, out var existing) &&
            existing.AllowRuntimeFallback)
            return existing.Type;
        return null;
    }

    private ITypeSymbol? GetEffectiveTrailingResultType(Compilation compilation)
    {
        var tree = compilation.SyntaxTrees.Last();
        var root = tree.GetCompilationUnitRoot();
        if (root.Members.LastOrDefault() is not GlobalStatementSyntax
            {
                Statement: ExpressionStatementSyntax expression
            })
            return null;
        var model = compilation.GetSemanticModel(tree);
        var type = model.GetTypeInfo(expression.Expression).ConvertedType;
        return type?.TypeKind == TypeKind.Dynamic
            ? TryGetBuiltInFlowStaticType(expression.Expression, model) ?? type
            : type;
    }

    private bool IsBuiltInDynamicFlow(ExpressionSyntax? expression, SemanticModel model)
    {
        if (expression is null) return false;
        foreach (var identifier in expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            if (variableStaticTypes.TryGetValue(identifier.Identifier.ValueText, out var existing) &&
                existing.AllowRuntimeFallback)
                return true;
            if (IsSessionGlobalsProperty(identifier, model))
                return true;
            if (identifier.Identifier.ValueText is "Last" or "Out" or "Data" &&
                model.GetSymbolInfo(identifier).Symbol is null)
                return true;
        }
        return false;
    }

    private static bool IsSessionGlobalsProperty(ExpressionSyntax expression, SemanticModel model) =>
        model.GetSymbolInfo(expression).Symbol is IPropertySymbol
        {
            Name: "Last" or "Out" or "Data",
            ContainingType.Name: "SessionGlobals"
        };

    private static string? GetCompletionTypeExpression(
        VariableStaticType? staticType,
        ResultSnapshot snapshot)
    {
        if (staticType is null) return null;
        return TryFormatStaticType(staticType.Type) ??
               (staticType.AllowRuntimeFallback ? snapshot.TypeExpression : null);
    }

    private static string? TryFormatStaticType(ITypeSymbol type) =>
        type.TypeKind is TypeKind.Dynamic or TypeKind.Error || ContainsAnonymousType(type)
            ? null
            : FormatTypeExpression(type);

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

    private static ResultSnapshot CreateRetainedStreamedSnapshot(
        IReadOnlyList<(int SourceIndex, ResultSnapshot Snapshot)> entries)
    {
        var display = $"{entries.Count:N0} items";
        var captured = new List<ResultSnapshot>();
        var indexes = new List<int>();
        var remainingNodes = MaximumVariableSnapshotNodes - 1;
        var remainingTextCharacters = MaximumVariableSnapshotTextCharacters - display.Length;
        foreach (var entry in entries)
        {
            var usage = MeasureSnapshot(entry.Snapshot);
            if (usage.Nodes > remainingNodes || usage.TextCharacters > remainingTextCharacters)
                break;
            captured.Add(entry.Snapshot);
            indexes.Add(entry.SourceIndex);
            remainingNodes -= usage.Nodes;
            remainingTextCharacters -= usage.TextCharacters;
        }

        return new(
            SnapshotKind.Sequence,
            display,
            null,
            Items: captured,
            IsTruncated: captured.Count < entries.Count,
            TotalCount: entries.Count,
            ItemIndexes: indexes);
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

        return FormatTypeExpression(type);
    }

    private static string FormatTypeExpression(ITypeSymbol type)
    {
        var format = SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
        return type.ToDisplayString(format);
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

    private sealed record VariableStaticType(ITypeSymbol Type, bool AllowRuntimeFallback);

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
