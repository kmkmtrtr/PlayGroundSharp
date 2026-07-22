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

/// <summary>Owns the mutable Roslyn ScriptState for one Worker session.</summary>
public sealed class ScriptSession
{
    private const int MaximumTransferredDiagnostics = 100;
    private const int MaximumVariableDisplayLength = 512;
    private const int MaximumVariableSnapshotNodes = 512;
    private const int MaximumVariableSnapshotTextCharacters = 256 * 1024;
    private const int MaximumVariableSnapshotTotalNodes = 50_000;
    private const int MaximumVariableSnapshotTotalTextCharacters = 10 * 1024 * 1024;
    private readonly SessionGlobals globals = new();
    private readonly ResultSnapshotFactory snapshots = new();
    private readonly List<string> submissions = [];
    private readonly List<string> imports = [.. SessionContext.DefaultImports];
    private readonly List<string> references = [];
    private readonly Dictionary<string, (string Identity, string Path)> assemblyIdentities = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> resolvedAssemblyNames = new(StringComparer.OrdinalIgnoreCase);
    private ScriptState<object?>? state;
    private ScriptOptions options;

    public ScriptSession()
    {
        options = ScriptOptions.Default
            .WithImports(imports)
            .AddReferences(
                typeof(object).Assembly,
                typeof(Enumerable).Assembly,
                typeof(SessionGlobals).Assembly,
                typeof(JsonElement).Assembly,
                typeof(Microsoft.CSharp.RuntimeBinder.Binder).Assembly,
                typeof(System.Numerics.BigInteger).Assembly);
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

    public async Task<ScriptExecutionResult> ExecuteAsync(
        int submissionIndex,
        string code,
        Action<string>? standardOutput = null,
        Action<string>? standardError = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var outputWriter = new BoundedEventWriter(standardOutput);
        using var errorWriter = new BoundedEventWriter(standardError);
        Console.SetOut(outputWriter);
        Console.SetError(errorWriter);
        globals.ExecutionCancellation = cancellationToken;
        try
        {
            var executableCode = PrepareSubmission(code);
            ScriptState<object?> candidate;
            try
            {
                candidate = state is null
                    ? await CSharpScript.Create<object?>(executableCode, options, typeof(SessionGlobals))
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

            var hasReturnValue = HasTrailingValueExpression(candidate.Script.GetCompilation());
            ResultSnapshot? snapshot = null;
            if (hasReturnValue)
            {
                try
                {
                    snapshot = snapshots.Create(candidate.ReturnValue, cancellationToken);
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
                globals.Last = candidate.ReturnValue;
                globals.Out.Set(submissionIndex, candidate.ReturnValue);
            }

            return new(true, hasReturnValue, candidate.ReturnValue, snapshot, [], null);
        }
        finally
        {
            outputWriter.Flush();
            errorWriter.Flush();
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            globals.ExecutionCancellation = default;
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
        var platformReferences = PlatformReferenceResolver.ResolveRuntimeDependencies([fullPath])
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
        globals.Last = null;
        globals.Out.Clear();
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
            var display = snapshot.Display;
            var truncated = display?.Length > MaximumVariableDisplayLength;
            return snapshot with
            {
                Display = truncated ? display![..MaximumVariableDisplayLength] : display,
                IsTruncated = snapshot.IsTruncated || truncated
            };
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
