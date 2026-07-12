using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlayGroundSharp.Core;
using PlayGroundSharp.LanguageService;

namespace PlayGroundSharp.App;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly WorkerClient worker = new();
    private readonly CSharpLanguageService languageService = new();
    private readonly List<string> submissions = [];
    private readonly List<string> imports = [.. SessionContext.DefaultImports];
    private readonly List<string> references = [];
    private readonly List<(string Id, string Version)> packages = [];
    private readonly List<string> history = [];
    private CancellationTokenSource? diagnosticDelay;
    private int historyPosition;
    private string? executingCode;

    [ObservableProperty] private string inputText = string.Empty;
    [ObservableProperty] private string status = "Starting Worker";
    [ObservableProperty] private string sessionStatus = "0 submissions";
    [ObservableProperty] private string diagnosticStatus = "0 errors, 0 warnings";
    [ObservableProperty] private string cursorStatus = "Ln 1, Col 1";
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private bool isReferenceDrawerOpen;

    public ObservableCollection<TranscriptLine> Transcript { get; } = [];
    public ObservableCollection<string> PackageItems { get; } = [];
    public ObservableCollection<string> ReferenceItems { get; } = [];
    public ObservableCollection<string> UsingItems { get; } = [.. SessionContext.DefaultImports];
    public IAsyncRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand ResetCommand { get; }
    public IAsyncRelayCommand RestartWorkerCommand { get; }
    public IRelayCommand ClearCommand { get; }
    public IRelayCommand ToggleReferenceDrawerCommand { get; }

    public MainViewModel()
    {
        CancelCommand = new AsyncRelayCommand(CancelAsync);
        ResetCommand = new AsyncRelayCommand(ResetAsync);
        RestartWorkerCommand = new AsyncRelayCommand(RestartWorkerAsync);
        ClearCommand = new RelayCommand(Transcript.Clear);
        ToggleReferenceDrawerCommand = new RelayCommand(() => IsReferenceDrawerOpen = !IsReferenceDrawerOpen);
        worker.EventReceived += envelope => Application.Current.Dispatcher.Invoke(() => HandleWorkerEvent(envelope));
        worker.Disconnected += message => Application.Current.Dispatcher.Invoke(() =>
        {
            Status = "Worker disconnected";
            IsRunning = false;
            Transcript.Add(TranscriptLine.System(message));
        });
    }

    public async Task InitializeAsync()
    {
        try
        {
            await worker.StartAsync();
            Status = "Ready";
            Transcript.Add(TranscriptLine.System("Worker connected. Do not execute untrusted code or packages."));
        }
        catch (Exception error)
        {
            Status = "Worker disconnected";
            Transcript.Add(TranscriptLine.Diagnostic(error.Message));
        }
    }

    public async Task ExecuteAsync()
    {
        var code = InputText;
        if (IsRunning || string.IsNullOrWhiteSpace(code)) return;
        if (code.TrimStart().StartsWith(':'))
        {
            try
            {
                await ExecuteCommandAsync(code.Trim());
            }
            catch (Exception error)
            {
                Status = "Ready";
                Transcript.Add(TranscriptLine.Diagnostic(error.Message));
            }
            return;
        }

        var index = submissions.Count + 1;
        executingCode = code;
        history.Add(code);
        historyPosition = history.Count;
        Transcript.Add(TranscriptLine.Input(index, code));
        InputText = string.Empty;
        IsRunning = true;
        Status = "Compiling";
        try
        {
            await worker.ExecuteAsync(index, code);
        }
        catch (Exception error)
        {
            Transcript.Add(TranscriptLine.Diagnostic(error.Message));
            Status = "Worker disconnected";
            IsRunning = false;
        }
    }

    public async Task CancelAsync()
    {
        if (!IsRunning) return;
        Status = "Cancelling";
        await worker.CancelAsync();
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        if (!IsRunning) return;
        await RestartAndRehydrateAsync();
        IsRunning = false;
        Status = "Ready";
        submissions.Clear();
        executingCode = null;
        SessionStatus = "0 submissions — Worker state lost";
        Transcript.Add(TranscriptLine.System("Worker was terminated because cancellation did not complete. Session variables were lost; transcript and input history were preserved."));
    }

    public string? MoveHistory(int delta)
    {
        if (history.Count == 0) return null;
        historyPosition = Math.Clamp(historyPosition + delta, 0, history.Count);
        return historyPosition == history.Count ? string.Empty : history[historyPosition];
    }

    public Task<IReadOnlyList<CompletionCandidate>> GetCompletionsAsync(int position) =>
        languageService.GetCompletionsAsync(Context, InputText, Math.Clamp(position, 0, InputText.Length));

    public Task<SignatureHelpResult?> GetSignatureHelpAsync(int position) =>
        languageService.GetSignatureHelpAsync(Context, InputText, Math.Clamp(position, 0, InputText.Length));

    public Task<QuickInfoResult?> GetQuickInfoAsync(int position) =>
        languageService.GetQuickInfoAsync(Context, InputText, Math.Clamp(position, 0, InputText.Length));

    partial void OnInputTextChanged(string value)
    {
        diagnosticDelay?.Cancel();
        diagnosticDelay?.Dispose();
        diagnosticDelay = new CancellationTokenSource();
        _ = UpdateDiagnosticsAfterDelayAsync(value, diagnosticDelay.Token);
    }

    private SessionContext Context => new([.. submissions], [.. imports], [.. references]);

    private async Task UpdateDiagnosticsAfterDelayAsync(string code, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(250, cancellationToken);
            var diagnostics = await languageService.GetDiagnosticsAsync(Context, code, cancellationToken);
            var errors = diagnostics.Count(static item => item.Level == DiagnosticLevel.Error);
            var warnings = diagnostics.Count(static item => item.Level == DiagnosticLevel.Warning);
            DiagnosticStatus = $"{errors} errors, {warnings} warnings";
        }
        catch (OperationCanceledException) { }
    }

    private void HandleWorkerEvent(PipeEnvelope envelope)
    {
        switch (envelope.Kind)
        {
            case MessageKinds.Started:
                Status = "Running";
                break;
            case MessageKinds.ConsoleOut:
                Transcript.Add(TranscriptLine.Console(envelope.ReadPayload<ConsoleEvent>().Text, false));
                break;
            case MessageKinds.ConsoleError:
                Transcript.Add(TranscriptLine.Console(envelope.ReadPayload<ConsoleEvent>().Text, true));
                break;
            case MessageKinds.Diagnostics:
                foreach (var diagnostic in envelope.ReadPayload<DiagnosticsEvent>().Diagnostics)
                    Transcript.Add(TranscriptLine.Diagnostic($"{diagnostic.Id} ({diagnostic.StartLine},{diagnostic.StartColumn}): {diagnostic.Message}", diagnostic.Level == DiagnosticLevel.Error));
                break;
            case MessageKinds.Result:
                var result = envelope.ReadPayload<ResultEvent>();
                Transcript.Add(TranscriptLine.Output(result.SubmissionIndex, FormatSnapshot(result.Snapshot)));
                break;
            case MessageKinds.RuntimeError:
                var exception = envelope.ReadPayload<RuntimeErrorEvent>().Exception;
                Transcript.Add(TranscriptLine.Diagnostic($"{exception.TypeName}: {exception.Message}"));
                break;
            case MessageKinds.Completed:
                var completed = envelope.ReadPayload<ExecutionCompletedEvent>();
                if (completed.StateAccepted && executingCode is not null) submissions.Add(executingCode);
                executingCode = null;
                IsRunning = false;
                Status = "Ready";
                SessionStatus = $"{submissions.Count} submissions — Worker {completed.WorkerMemoryBytes / 1024 / 1024:N0} MiB";
                break;
            case MessageKinds.Cancelled:
                IsRunning = false;
                Status = "Ready";
                Transcript.Add(TranscriptLine.System("Execution cancelled."));
                break;
            case MessageKinds.Error:
                Transcript.Add(TranscriptLine.Diagnostic(envelope.ReadPayload<WorkerErrorEvent>().Message));
                IsRunning = false;
                Status = "Ready";
                break;
            case MessageKinds.PackageProgress:
                Status = "Restoring package";
                Transcript.Add(TranscriptLine.System(envelope.ReadPayload<PackageProgressEvent>().Message));
                break;
            case MessageKinds.PackageAdded:
                var package = envelope.ReadPayload<PackageAddedEvent>();
                packages.RemoveAll(item => item.Id.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase));
                packages.Add((package.PackageId, package.Version));
                PackageItems.Clear();
                foreach (var item in packages) PackageItems.Add($"{item.Id} {item.Version}");
                foreach (var path in package.AssemblyPaths)
                    if (!references.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        references.Add(path);
                        ReferenceItems.Add(path);
                    }
                Transcript.Add(TranscriptLine.System($"Package added: {package.PackageId} {package.Version}"));
                break;
        }
    }

    private async Task ResetAsync()
    {
        if (IsRunning) await CancelAsync();
        await worker.ResetAsync();
        submissions.Clear();
        SessionStatus = "0 submissions";
        Transcript.Add(TranscriptLine.System("Session reset."));
    }

    private async Task RestartWorkerAsync()
    {
        Status = "Starting Worker";
        await RestartAndRehydrateAsync();
        submissions.Clear();
        executingCode = null;
        IsRunning = false;
        Status = "Ready";
        SessionStatus = "0 submissions — Worker restarted";
        Transcript.Add(TranscriptLine.System("Worker restarted. Variables and methods were lost; references and usings were restored."));
    }

    private async Task RestartAndRehydrateAsync()
    {
        await worker.RestartAsync();
        foreach (var import in imports.Except(SessionContext.DefaultImports, StringComparer.Ordinal))
            await worker.AddUsingAsync(import);
        foreach (var reference in references)
            await worker.AddReferenceAsync(reference);
    }

    private async Task ExecuteCommandAsync(string command)
    {
        if (command == ":clear")
        {
            Transcript.Clear();
        }
        else if (command == ":reset")
        {
            await ResetAsync();
        }
        else if (command == ":using list")
        {
            Transcript.Add(TranscriptLine.System(string.Join(Environment.NewLine, imports)));
        }
        else if (command.StartsWith(":using add ", StringComparison.Ordinal))
        {
            var value = command[":using add ".Length..].Trim();
            await worker.AddUsingAsync(value);
            if (!imports.Contains(value, StringComparer.Ordinal))
            {
                imports.Add(value);
                UsingItems.Add(value);
            }
            Transcript.Add(TranscriptLine.System($"Using added: {value}"));
        }
        else if (command == ":reference list")
        {
            Transcript.Add(TranscriptLine.System(references.Count == 0 ? "No dynamic references." : string.Join(Environment.NewLine, references)));
        }
        else if (command.StartsWith(":reference add ", StringComparison.Ordinal))
        {
            var match = Regex.Match(command, "^:reference add \\\"(?<path>.+)\\\"$");
            if (!match.Success) throw new ArgumentException("Usage: :reference add \"<DLL path>\"");
            var path = Path.GetFullPath(match.Groups["path"].Value);
            await worker.AddReferenceAsync(path);
            if (!references.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                references.Add(path);
                ReferenceItems.Add(path);
            }
            Transcript.Add(TranscriptLine.System($"Reference added: {path}"));
        }
        else if (command == ":package list")
        {
            Transcript.Add(TranscriptLine.System(packages.Count == 0 ? "No packages." :
                string.Join(Environment.NewLine, packages.Select(static item => $"{item.Id} {item.Version}"))));
        }
        else if (command.StartsWith(":package add ", StringComparison.Ordinal))
        {
            var match = Regex.Match(command, "^:package add (?<id>[A-Za-z0-9._-]+)(?: --version (?<version>[^ ]+))?$");
            if (!match.Success) throw new ArgumentException("Usage: :package add <PackageId> [--version <Version>]");
            var version = match.Groups["version"].Success ? match.Groups["version"].Value : null;
            Status = "Restoring package";
            await worker.AddPackageAsync(match.Groups["id"].Value, version);
            Status = "Ready";
        }
        else
        {
            Transcript.Add(TranscriptLine.Diagnostic($"Unknown command: {command}"));
        }
        InputText = string.Empty;
    }

    private static string FormatSnapshot(ResultSnapshot snapshot)
    {
        var suffix = snapshot.IsTruncated ? " … (truncated)" : string.Empty;
        return (snapshot.Display ?? snapshot.Kind.ToString()) + suffix + (snapshot.TypeName is null ? string.Empty : $"  [{snapshot.TypeName}]");
    }

    public async ValueTask DisposeAsync()
    {
        diagnosticDelay?.Cancel();
        diagnosticDelay?.Dispose();
        await worker.DisposeAsync();
    }
}
