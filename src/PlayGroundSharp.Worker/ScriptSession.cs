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
    ExceptionInfo? Exception);

/// <summary>Owns the mutable Roslyn ScriptState for one Worker session.</summary>
public sealed class ScriptSession
{
    private readonly SessionGlobals globals = new();
    private readonly ResultSnapshotFactory snapshots = new();
    private readonly List<string> submissions = [];
    private readonly List<string> imports = [.. SessionContext.DefaultImports];
    private readonly List<string> references = [];
    private readonly Dictionary<string, (string Identity, string Path)> assemblyIdentities = new(StringComparer.OrdinalIgnoreCase);
    private ScriptState<object?>? state;
    private ScriptOptions options;

    public ScriptSession()
    {
        options = ScriptOptions.Default
            .WithImports(imports)
            .AddReferences(typeof(object).Assembly, typeof(Enumerable).Assembly, typeof(SessionGlobals).Assembly, typeof(JsonElement).Assembly);
    }

    public SessionContext Context => new([.. submissions], [.. imports], [.. references]);

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
        try
        {
            ScriptState<object?> candidate;
            try
            {
                candidate = state is null
                    ? await CSharpScript.Create<object?>(code, options, typeof(SessionGlobals))
                        .RunAsync(globals, static _ => true, cancellationToken).ConfigureAwait(false)
                    : await state.ContinueWithAsync<object?>(code, options, static _ => true, cancellationToken).ConfigureAwait(false);
            }
            catch (CompilationErrorException error)
            {
                return new(false, false, null, null, error.Diagnostics.Select(ToDiagnostic).ToArray(), null);
            }

            state = candidate;
            submissions.Add(code);
            if (candidate.Exception is not null)
            {
                return new(true, false, null, null, [], ResultSnapshotFactory.CreateException(candidate.Exception));
            }

            var hasReturnValue = HasTrailingExpression(code, candidate.Script.GetCompilation());
            if (hasReturnValue)
            {
                globals.Last = candidate.ReturnValue;
                globals.Out.Set(submissionIndex, candidate.ReturnValue);
            }

            return new(true, hasReturnValue, candidate.ReturnValue,
                hasReturnValue ? snapshots.Create(candidate.ReturnValue) : null, [], null);
        }
        finally
        {
            outputWriter.Flush();
            errorWriter.Flush();
            Console.SetOut(originalOut);
            Console.SetError(originalError);
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
        options = options.AddReferences(MetadataReference.CreateFromFile(fullPath));
        references.Add(fullPath);
        assemblyIdentities[simpleName] = (identity, fullPath);
    }

    public void AddUsing(string @namespace)
    {
        if (string.IsNullOrWhiteSpace(@namespace) || !SyntaxFacts.IsValidIdentifier(@namespace.Replace(".", string.Empty, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Namespace is invalid.", nameof(@namespace));
        }
        if (!imports.Contains(@namespace, StringComparer.Ordinal))
        {
            imports.Add(@namespace);
            options = options.AddImports(@namespace);
        }
    }

    public void Reset()
    {
        state = null;
        submissions.Clear();
        globals.Last = null;
        globals.Out.Clear();
    }

    private static bool HasTrailingExpression(string code, Compilation compilation)
    {
        var tree = compilation.SyntaxTrees.Last();
        var root = tree.GetCompilationUnitRoot();
        return root.Members.LastOrDefault() is GlobalStatementSyntax
        {
            Statement: ExpressionStatementSyntax expression
        } && expression.SemicolonToken.IsMissing;
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
