using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlayGroundSharp.Core;
using PlayGroundSharp.LanguageService;

namespace PlayGroundSharp.App;

public sealed record VariableItem(string Name, string TypeName, string Value, bool IsReadOnly)
{
    public string Kind => IsReadOnly ? "const" : "var";
}

public sealed record LibraryItem(string Kind, string Name, string Version, string Source, string? Path);
public sealed partial class UiOption<T>(T value, string label) : ObservableObject where T : struct, Enum
{
    public T Value { get; } = value;
    [ObservableProperty] private string label = label;
    public override string ToString() => Label;
}

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaximumExplorerSearchResults = 400;
    private static readonly string[] Commands =
    [
        ":help", ":clear", ":reset", ":using list", ":using add ", ":using remove ",
        ":reference list", ":reference add \"\"", ":package list", ":package add "
    ];
    private readonly WorkerClient worker = new();
    private readonly CSharpLanguageService languageService = new();
    private readonly List<string> submissions = [];
    private readonly List<string> imports = [.. SessionContext.DefaultImports];
    private readonly List<string> references = [];
    private readonly List<(string Id, string Version)> packages = [];
    private readonly List<string> history = [];
    private CancellationTokenSource? diagnosticDelay;
    private CancellationTokenSource? typeExplorerSearchDelay;
    private CancellationTokenSource? typeExplorerRefresh;
    private IReadOnlyList<SymbolExplorerEntry> typeExplorerEntries = [];
    private int diagnosticErrorCount;
    private int diagnosticWarningCount;
    private int cursorLine = 1;
    private int cursorColumn = 1;
    private string statusLocalizationKey = "Status.StartingWorker";
    private object?[] statusLocalizationArguments = [];
    private string sessionLocalizationKey = "Session.Count";
    private object?[] sessionLocalizationArguments = [0];
    private int historyPosition;
    private string historyDraft = string.Empty;
    private string? executingCode;
    private TaskCompletionSource? executionCompletion;

    [ObservableProperty] private string inputText = string.Empty;
    [ObservableProperty] private string status = "Starting Worker";
    [ObservableProperty] private string sessionStatus = "0 submissions";
    [ObservableProperty] private string diagnosticStatus = "0 errors, 0 warnings";
    [ObservableProperty] private string cursorStatus = "Ln 1, Col 1";
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private bool isReferenceDrawerOpen;
    [ObservableProperty] private bool isTypeExplorerOpen = true;
    [ObservableProperty] private string typeExplorerSearchText = string.Empty;
    [ObservableProperty] private string typeExplorerStatus = "Loading types…";
    [ObservableProperty] private SymbolExplorerNode? selectedExplorerNode;
    [ObservableProperty] private string packageSearchText = string.Empty;
    [ObservableProperty] private string packageSearchMessage = "Search packages on nuget.org";
    [ObservableProperty] private bool includePrereleasePackages;
    [ObservableProperty] private bool isPackageSearchBusy;
    [ObservableProperty] private bool isWorkspaceBusy;
    [ObservableProperty] private string newUsingText = string.Empty;
    [ObservableProperty] private ExecutionKeyMode executionKeyMode = SettingsStore.Load().ExecutionKeyMode;
    [ObservableProperty] private AppThemeMode themeMode = SettingsStore.Load().ThemeMode;
    [ObservableProperty] private AppLanguageMode languageMode = SettingsStore.Load().LanguageMode;

    public ObservableCollection<TranscriptLine> Transcript { get; } = [];
    public ObservableCollection<string> PackageItems { get; } = [];
    public ObservableCollection<string> ReferenceItems { get; } = [];
    public ObservableCollection<string> UsingItems { get; } = [.. SessionContext.DefaultImports];
    public ObservableCollection<VariableItem> VariableItems { get; } = [];
    public ObservableCollection<NuGetPackageInfo> PackageSearchItems { get; } = [];
    public ObservableCollection<LibraryItem> LibraryItems { get; } = [];
    public ObservableCollection<SymbolExplorerNode> TypeExplorerItems { get; } = [];
    public IReadOnlyList<UiOption<ExecutionKeyMode>> ExecutionKeyOptions { get; } =
    [
        new(ExecutionKeyMode.Enter, "Enter"),
        new(ExecutionKeyMode.ControlEnter, "Ctrl+Enter")
    ];
    public IReadOnlyList<UiOption<AppThemeMode>> ThemeOptions { get; } =
    [
        new(AppThemeMode.Light, string.Empty),
        new(AppThemeMode.Dark, string.Empty)
    ];
    public IReadOnlyList<UiOption<AppLanguageMode>> LanguageOptions { get; } =
    [
        new(AppLanguageMode.Japanese, "日本語"),
        new(AppLanguageMode.English, "English")
    ];
    public IAsyncRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand ResetCommand { get; }
    public IAsyncRelayCommand RestartWorkerCommand { get; }
    public IRelayCommand ClearCommand { get; }
    public IRelayCommand ToggleReferenceDrawerCommand { get; }
    public IRelayCommand ToggleTypeExplorerCommand { get; }
    public IAsyncRelayCommand SearchPackagesCommand { get; }
    public IAsyncRelayCommand<NuGetPackageInfo> InstallPackageCommand { get; }
    public IAsyncRelayCommand AddUsingFromGuiCommand { get; }

    public MainViewModel()
    {
        UpdateThemeOptionLabels(LanguageMode);
        SetLocalizedStatus("Status.StartingWorker");
        SetSessionStatus("Session.Count", 0);
        DiagnosticStatus = Localize("Diagnostics.Count", 0, 0);
        CursorStatus = Localize("Cursor.Position", 1, 1);
        TypeExplorerStatus = Localize("Explorer.Loading");
        PackageSearchMessage = Localize("Package.SearchPrompt");
        CancelCommand = new AsyncRelayCommand(CancelAsync);
        ResetCommand = new AsyncRelayCommand(ResetAsync);
        RestartWorkerCommand = new AsyncRelayCommand(RestartWorkerAsync);
        ClearCommand = new RelayCommand(Transcript.Clear);
        ToggleReferenceDrawerCommand = new RelayCommand(() => IsReferenceDrawerOpen = !IsReferenceDrawerOpen);
        ToggleTypeExplorerCommand = new RelayCommand(() => IsTypeExplorerOpen = !IsTypeExplorerOpen);
        SearchPackagesCommand = new AsyncRelayCommand(SearchPackagesAsync);
        InstallPackageCommand = new AsyncRelayCommand<NuGetPackageInfo>(InstallPackageAsync);
        AddUsingFromGuiCommand = new AsyncRelayCommand(AddUsingFromGuiAsync);
        worker.EventReceived += envelope => Application.Current.Dispatcher.Invoke(() => HandleWorkerEvent(envelope));
        worker.Disconnected += message => Application.Current.Dispatcher.Invoke(() =>
        {
            SetLocalizedStatus("Status.WorkerDisconnected");
            IsRunning = false;
            SignalExecutionFinished();
            VariableItems.Clear();
            Transcript.Add(TranscriptLine.System(message));
        });
    }

    public string Localize(string key, params object?[] arguments) =>
        AppLocalization.Text(LanguageMode, key, arguments);

    public WorkspaceDocument CreateWorkspaceDocument() => new(
        WorkspaceDocument.CurrentVersion,
        DateTime.UtcNow,
        [.. submissions],
        [.. imports],
        [.. references],
        packages.Select(static package => new WorkspacePackage(package.Id, package.Version)).ToArray(),
        InputText);

    public async Task LoadWorkspaceAsync(WorkspaceDocument document, CancellationToken cancellationToken = default)
    {
        if (IsWorkspaceBusy) return;
        IsWorkspaceBusy = true;
        SetLocalizedStatus("Status.LoadingWorkspace");
        try
        {
            if (IsRunning) await CancelAsync();
            await worker.RestartAsync(cancellationToken);
            submissions.Clear();
            imports.Clear();
            imports.AddRange(document.Imports.Distinct(StringComparer.Ordinal));
            references.Clear();
            packages.Clear();
            history.Clear();
            historyPosition = 0;
            historyDraft = string.Empty;
            executingCode = null;
            Transcript.Clear();
            PackageItems.Clear();
            ReferenceItems.Clear();
            UsingItems.Clear();
            foreach (var import in imports) UsingItems.Add(import);
            VariableItems.Clear();
            LibraryItems.Clear();
            SelectedExplorerNode = null;

            await ConfigureWorkerImportsAsync(cancellationToken);

            foreach (var package in document.Packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await worker.AddPackageAsync(package.Id, package.Version, cancellationToken);
            }

            foreach (var path in document.References.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (references.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
                if (!File.Exists(path))
                {
                    Transcript.Add(TranscriptLine.Diagnostic(Localize("Message.ReferenceMissing", path), false));
                    continue;
                }
                await worker.AddReferenceAsync(path, cancellationToken);
                references.Add(path);
                ReferenceItems.Add(path);
                AddAssemblyLibrary(path, "Workspace");
            }

            foreach (var code in document.Submissions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var expectedCount = submissions.Count + 1;
                executingCode = code;
                Transcript.Add(TranscriptLine.Input(expectedCount, code));
                executionCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                IsRunning = true;
                SetLocalizedStatus("Status.ReplayingWorkspace", expectedCount, document.Submissions.Count);
                await worker.ExecuteAsync(expectedCount, code, cancellationToken);
                if (submissions.Count != expectedCount)
                    throw new InvalidDataException(Localize("Message.ReplayFailed", expectedCount));
                history.Add(code);
            }

            historyPosition = history.Count;
            InputText = document.InputText;
            IsRunning = false;
            SetSessionStatus("Session.Count", submissions.Count);
            SetLocalizedStatus("Status.WorkspaceLoaded");
            Transcript.Add(TranscriptLine.System(Localize("Message.WorkspaceLoaded", submissions.Count)));
            await RefreshTypeExplorerAsync();
        }
        catch
        {
            IsRunning = false;
            SignalExecutionFinished();
            executingCode = null;
            SetLocalizedStatus("Status.WorkspaceLoadFailed");
            throw;
        }
        finally
        {
            IsWorkspaceBusy = false;
        }
    }

    public void SetLocalizedStatus(string key, params object?[] arguments)
    {
        statusLocalizationKey = key;
        statusLocalizationArguments = arguments;
        Status = Localize(key, arguments);
    }

    public void UpdateCursorPosition(int line, int column)
    {
        cursorLine = line;
        cursorColumn = column;
        CursorStatus = Localize("Cursor.Position", line, column);
    }

    private void SetSessionStatus(string key, params object?[] arguments)
    {
        sessionLocalizationKey = key;
        sessionLocalizationArguments = arguments;
        SessionStatus = Localize(key, arguments);
    }

    public async Task InitializeAsync()
    {
        try
        {
            await worker.StartAsync();
            SetLocalizedStatus("Status.Ready");
            Transcript.Add(TranscriptLine.System(Localize("Message.WorkerConnected")));
            await RefreshTypeExplorerAsync();
        }
        catch (Exception error)
        {
            SetLocalizedStatus("Status.WorkerDisconnected");
            Transcript.Add(TranscriptLine.Diagnostic(error.Message));
        }
    }

    public async Task ExecuteAsync()
    {
        var code = InputText;
        if (IsRunning || string.IsNullOrWhiteSpace(code)) return;
        if (code.TrimStart().StartsWith(':'))
        {
            InputText = string.Empty;
            try
            {
                await ExecuteCommandAsync(code.Trim());
            }
            catch (Exception error)
            {
                SetLocalizedStatus("Status.Ready");
                Transcript.Add(TranscriptLine.Diagnostic(error.Message));
            }
            return;
        }

        var index = submissions.Count + 1;
        executingCode = code;
        history.Add(code);
        historyPosition = history.Count;
        historyDraft = string.Empty;
        Transcript.Add(TranscriptLine.Input(index, code));
        InputText = string.Empty;
        executionCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IsRunning = true;
        SetLocalizedStatus("Status.Compiling");
        try
        {
            await worker.ExecuteAsync(index, code);
        }
        catch (Exception error)
        {
            Transcript.Add(TranscriptLine.Diagnostic(error.Message));
            SetLocalizedStatus("Status.WorkerDisconnected");
            IsRunning = false;
            SignalExecutionFinished();
        }
    }

    public async Task CancelAsync()
    {
        if (!IsRunning) return;
        SetLocalizedStatus("Status.Cancelling");
        var completion = executionCompletion?.Task;
        await worker.CancelAsync();
        var timeout = Task.Delay(TimeSpan.FromSeconds(1.5));
        if (completion is not null && await Task.WhenAny(completion, timeout) == completion) return;
        if (completion is null) await timeout;
        if (!IsRunning) return;
        await RestartAndRehydrateAsync();
        IsRunning = false;
        SignalExecutionFinished();
        SetLocalizedStatus("Status.Ready");
        submissions.Clear();
        executingCode = null;
        VariableItems.Clear();
        SetSessionStatus("Session.StateLost");
        Transcript.Add(TranscriptLine.System(Localize("Message.ForcedRestart")));
    }

    public string? MoveHistory(int delta, string currentInput)
    {
        if (history.Count == 0) return null;
        if (delta < 0 && historyPosition == history.Count) historyDraft = currentInput;
        historyPosition = Math.Clamp(historyPosition + delta, 0, history.Count);
        return historyPosition == history.Count ? historyDraft : history[historyPosition];
    }

    public Task<IReadOnlyList<CompletionCandidate>> GetCompletionsAsync(
        int position,
        CancellationToken cancellationToken = default)
    {
        var context = Context;
        var code = InputText;
        var offset = Math.Clamp(position, 0, code.Length);
        if (code.TrimStart().StartsWith(':')) return GetCommandCompletionsAsync(context, code, cancellationToken);
        return Task.Run(
            () => languageService.GetCompletionsAsync(context, code, offset, cancellationToken),
            cancellationToken);
    }

    private Task<IReadOnlyList<CompletionCandidate>> GetCommandCompletionsAsync(
        SessionContext context,
        string code,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        var candidates = Commands.Select(static command => new CompletionCandidate(
            command, command, command, ["Command"], command, ReplacementStart: 0)).ToList();
        const string usingPrefix = ":using add ";
        if (code.StartsWith(usingPrefix, StringComparison.Ordinal))
        {
            candidates.AddRange(languageService.GetReferenceNamespaces(context).Select(@namespace =>
            {
                var command = usingPrefix + @namespace;
                return new CompletionCandidate(command, command, command, ["Namespace"], command, ReplacementStart: 0);
            }));
        }
        const string removeUsingPrefix = ":using remove ";
        if (code.StartsWith(removeUsingPrefix, StringComparison.Ordinal))
        {
            candidates.AddRange(context.Imports.Select(@namespace =>
            {
                var command = removeUsingPrefix + @namespace;
                return new CompletionCandidate(command, command, command, ["Namespace"], command, ReplacementStart: 0);
            }));
        }
        return (IReadOnlyList<CompletionCandidate>)candidates;
    }, cancellationToken);

    public Task<string?> GetCompletionDescriptionAsync(int position, CompletionCandidate candidate, CancellationToken cancellationToken)
    {
        if (candidate.Tags.Contains("Command", StringComparer.Ordinal))
            return Task.FromResult<string?>(Localize("Completion.Command"));
        if (candidate.Tags.Contains("Namespace", StringComparer.Ordinal))
            return Task.FromResult<string?>(Localize("Completion.Namespace"));
        var context = Context;
        var code = InputText;
        var offset = Math.Clamp(position, 0, code.Length);
        return Task.Run(
            () => languageService.GetCompletionDescriptionAsync(context, code, offset, candidate, cancellationToken),
            cancellationToken);
    }

    public Task<SignatureHelpResult?> GetSignatureHelpAsync(int position, CancellationToken cancellationToken = default)
    {
        var context = Context;
        var code = InputText;
        var offset = Math.Clamp(position, 0, code.Length);
        return Task.Run(
            () => languageService.GetSignatureHelpAsync(context, code, offset, cancellationToken),
            cancellationToken);
    }

    public Task<QuickInfoResult?> GetQuickInfoAsync(int position)
    {
        var context = Context;
        var code = InputText;
        var offset = Math.Clamp(position, 0, code.Length);
        return Task.Run(() => languageService.GetQuickInfoAsync(context, code, offset));
    }

    public async Task<bool> AddUsingAsync(string @namespace)
    {
        if (IsRunning) throw new InvalidOperationException(Localize("Message.SessionBusy"));
        var value = @namespace.Trim();
        if (imports.Contains(value, StringComparer.Ordinal)) return false;
        await worker.AddUsingAsync(value);
        imports.Add(value);
        UsingItems.Add(value);
        await RefreshTypeExplorerAsync();
        return true;
    }

    public bool HasSessionState => submissions.Count > 0;

    public async Task<bool> RemoveUsingAsync(string? @namespace)
    {
        var value = @namespace?.Trim();
        if (string.IsNullOrEmpty(value) || !imports.Contains(value, StringComparer.Ordinal)) return false;
        if (IsRunning) return false;

        var rebuildWorker = HasSessionState;
        if (rebuildWorker)
        {
            imports.Remove(value);
            UsingItems.Remove(value);
            SetLocalizedStatus("Status.StartingWorker");
            await RestartAndRehydrateAsync();
            submissions.Clear();
            executingCode = null;
            VariableItems.Clear();
            IsRunning = false;
            SetSessionStatus("Session.Restarted");
            Transcript.Add(TranscriptLine.System(Localize("Message.UsingRemovedWithRestart", value)));
        }
        else
        {
            await worker.RemoveUsingAsync(value);
            imports.Remove(value);
            UsingItems.Remove(value);
            Transcript.Add(TranscriptLine.System(Localize("Message.UsingRemoved", value)));
        }

        SetLocalizedStatus("Status.RemovedUsing", value);
        await RefreshTypeExplorerAsync();
        return true;
    }

    private async Task AddUsingFromGuiAsync()
    {
        var value = NewUsingText.Trim();
        if (value.Length == 0) return;
        try
        {
            var added = await AddUsingAsync(value);
            Transcript.Add(TranscriptLine.System(added
                ? Localize("Message.UsingAdded", value)
                : Localize("Message.UsingActive", value)));
            if (added) SetLocalizedStatus("Status.AddedUsing", value);
            NewUsingText = string.Empty;
        }
        catch (Exception error)
        {
            SetLocalizedStatus("Status.UsingFailed");
            Transcript.Add(TranscriptLine.Diagnostic(error.Message));
        }
    }

    partial void OnInputTextChanged(string value)
    {
        diagnosticDelay?.Cancel();
        diagnosticDelay?.Dispose();
        diagnosticDelay = new CancellationTokenSource();
        _ = UpdateDiagnosticsAfterDelayAsync(value, diagnosticDelay.Token);
    }

    partial void OnExecutionKeyModeChanged(ExecutionKeyMode value) => SettingsStore.Save(new(value, ThemeMode, LanguageMode));

    partial void OnTypeExplorerSearchTextChanged(string value) => ScheduleTypeExplorerRebuild();

    partial void OnThemeModeChanged(AppThemeMode value)
    {
        SettingsStore.Save(new(ExecutionKeyMode, value, LanguageMode));
        App.ApplyTheme(value);
        for (var index = 0; index < Transcript.Count; index++)
            Transcript[index] = Transcript[index].WithCurrentTheme();
    }

    partial void OnLanguageModeChanged(AppLanguageMode value)
    {
        SettingsStore.Save(new(ExecutionKeyMode, ThemeMode, value));
        App.ApplyLanguage(value);
        UpdateThemeOptionLabels(value);
        Status = Localize(statusLocalizationKey, statusLocalizationArguments);
        SessionStatus = Localize(sessionLocalizationKey, sessionLocalizationArguments);
        DiagnosticStatus = Localize("Diagnostics.Count", diagnosticErrorCount, diagnosticWarningCount);
        CursorStatus = Localize("Cursor.Position", cursorLine, cursorColumn);
        RebuildTypeExplorer();
    }

    private void UpdateThemeOptionLabels(AppLanguageMode languageMode)
    {
        ThemeOptions[0].Label = languageMode == AppLanguageMode.Japanese ? "ライト" : "Light";
        ThemeOptions[1].Label = languageMode == AppLanguageMode.Japanese ? "ダーク" : "Dark";
    }

    private SessionContext Context => new([.. submissions], [.. imports], [.. references]);

    private async Task UpdateDiagnosticsAfterDelayAsync(string code, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(250, cancellationToken);
            var context = Context;
            var diagnostics = await Task.Run(
                () => languageService.GetDiagnosticsAsync(context, code, cancellationToken), cancellationToken);
            var errors = diagnostics.Count(static item => item.Level == DiagnosticLevel.Error);
            var warnings = diagnostics.Count(static item => item.Level == DiagnosticLevel.Warning);
            diagnosticErrorCount = errors;
            diagnosticWarningCount = warnings;
            DiagnosticStatus = Localize("Diagnostics.Count", errors, warnings);
        }
        catch (OperationCanceledException) { }
    }

    private void HandleWorkerEvent(PipeEnvelope envelope)
    {
        switch (envelope.Kind)
        {
            case MessageKinds.Started:
                SetLocalizedStatus("Status.Running");
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
                Transcript.Add(TranscriptLine.Output(result.SubmissionIndex, FormatResultSnapshot(result.Snapshot), result.Snapshot));
                break;
            case MessageKinds.RuntimeError:
                var exception = envelope.ReadPayload<RuntimeErrorEvent>().Exception;
                Transcript.Add(TranscriptLine.Diagnostic($"{exception.TypeName}: {exception.Message}"));
                break;
            case MessageKinds.Variables:
                VariableItems.Clear();
                foreach (var variable in envelope.ReadPayload<VariablesEvent>().Variables)
                    VariableItems.Add(new(variable.Name, variable.TypeName, FormatSnapshot(variable.Value), variable.IsReadOnly));
                break;
            case MessageKinds.Completed:
                var completed = envelope.ReadPayload<ExecutionCompletedEvent>();
                if (completed.StateAccepted && executingCode is not null)
                {
                    submissions.Add(executingCode);
                    if (!IsWorkspaceBusy) _ = RefreshTypeExplorerAsync();
                }
                executingCode = null;
                IsRunning = false;
                SignalExecutionFinished();
                SetLocalizedStatus("Status.Ready");
                SetSessionStatus("Session.Memory", submissions.Count, completed.WorkerMemoryBytes / 1024 / 1024);
                break;
            case MessageKinds.Cancelled:
                IsRunning = false;
                SignalExecutionFinished();
                SetLocalizedStatus("Status.Ready");
                Transcript.Add(TranscriptLine.System(Localize("Message.ExecutionCancelled")));
                break;
            case MessageKinds.Error:
                Transcript.Add(TranscriptLine.Diagnostic(envelope.ReadPayload<WorkerErrorEvent>().Message));
                IsRunning = false;
                SignalExecutionFinished();
                SetLocalizedStatus("Status.Ready");
                break;
            case MessageKinds.PackageProgress:
                SetLocalizedStatus("Status.RestoringPackage");
                Transcript.Add(TranscriptLine.System(envelope.ReadPayload<PackageProgressEvent>().Message));
                break;
            case MessageKinds.PackageAdded:
                var package = envelope.ReadPayload<PackageAddedEvent>();
                packages.RemoveAll(item => item.Id.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase));
                packages.Add((package.PackageId, package.Version));
                PackageItems.Clear();
                foreach (var item in packages) PackageItems.Add($"{item.Id} {item.Version}");
                AddPackageLibrary(package);
                foreach (var path in package.AssemblyPaths)
                    if (!references.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        references.Add(path);
                        ReferenceItems.Add(path);
                    }
                if (!IsWorkspaceBusy) _ = RefreshTypeExplorerAsync();
                Transcript.Add(TranscriptLine.System(Localize("Message.PackageAdded", package.PackageId, package.Version)));
                break;
            case MessageKinds.PackageSearchResults:
                var searchResults = envelope.ReadPayload<PackageSearchResultsEvent>();
                PackageSearchItems.Clear();
                foreach (var item in searchResults.Packages) PackageSearchItems.Add(item);
                PackageSearchMessage = searchResults.TotalHits == 0
                    ? Localize("Package.NoneFound")
                    : Localize("Package.Found", searchResults.TotalHits, searchResults.Packages.Count);
                break;
        }
    }

    private async Task ResetAsync()
    {
        if (IsRunning) await CancelAsync();
        await worker.ResetAsync();
        submissions.Clear();
        VariableItems.Clear();
        SetSessionStatus("Session.Count", 0);
        Transcript.Add(TranscriptLine.System(Localize("Message.SessionReset")));
        await RefreshTypeExplorerAsync();
    }

    private async Task RestartWorkerAsync()
    {
        SetLocalizedStatus("Status.StartingWorker");
        await RestartAndRehydrateAsync();
        submissions.Clear();
        executingCode = null;
        VariableItems.Clear();
        IsRunning = false;
        SignalExecutionFinished();
        SetLocalizedStatus("Status.Ready");
        SetSessionStatus("Session.Restarted");
        Transcript.Add(TranscriptLine.System(Localize("Message.WorkerRestarted")));
        await RefreshTypeExplorerAsync();
    }

    private async Task RestartAndRehydrateAsync()
    {
        await worker.RestartAsync();
        await ConfigureWorkerImportsAsync();
        foreach (var reference in references)
            await worker.AddReferenceAsync(reference);
    }

    private async Task ConfigureWorkerImportsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var import in SessionContext.DefaultImports.Except(imports, StringComparer.Ordinal))
            await worker.RemoveUsingAsync(import, cancellationToken);
        foreach (var import in imports.Except(SessionContext.DefaultImports, StringComparer.Ordinal))
            await worker.AddUsingAsync(import, cancellationToken);
    }

    private async Task ExecuteCommandAsync(string command)
    {
        if (command == ":help")
        {
            Transcript.Add(TranscriptLine.System(string.Join(Environment.NewLine, Commands)));
        }
        else if (command == ":clear")
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
            var added = await AddUsingAsync(value);
            Transcript.Add(TranscriptLine.System(added
                ? Localize("Message.UsingAdded", value)
                : Localize("Message.UsingActive", value)));
        }
        else if (command.StartsWith(":using remove ", StringComparison.Ordinal))
        {
            var value = command[":using remove ".Length..].Trim();
            if (!await RemoveUsingAsync(value))
                Transcript.Add(TranscriptLine.System(Localize("Message.UsingMissing", value)));
        }
        else if (command == ":reference list")
        {
            Transcript.Add(TranscriptLine.System(references.Count == 0
                ? Localize("Message.NoReferences")
                : string.Join(Environment.NewLine, references)));
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
                AddAssemblyLibrary(path, "Local DLL");
            }
            await RefreshTypeExplorerAsync();
            Transcript.Add(TranscriptLine.System(Localize("Message.ReferenceAdded", path)));
        }
        else if (command == ":package list")
        {
            Transcript.Add(TranscriptLine.System(packages.Count == 0 ? Localize("Message.NoPackages") :
                string.Join(Environment.NewLine, packages.Select(static item => $"{item.Id} {item.Version}"))));
        }
        else if (command.StartsWith(":package add ", StringComparison.Ordinal))
        {
            var match = Regex.Match(command, "^:package add (?<id>[A-Za-z0-9._-]+)(?: --version (?<version>[^ ]+))?$");
            if (!match.Success) throw new ArgumentException("Usage: :package add <PackageId> [--version <Version>]");
            var version = match.Groups["version"].Success ? match.Groups["version"].Value : null;
            await AddPackageAsync(match.Groups["id"].Value, version);
        }
        else
        {
            Transcript.Add(TranscriptLine.Diagnostic(Localize("Message.UnknownCommand", command)));
        }
        InputText = string.Empty;
    }

    private async Task SearchPackagesAsync()
    {
        if (IsPackageSearchBusy) return;
        if (IsRunning)
        {
            PackageSearchMessage = Localize("Message.SessionBusy");
            return;
        }
        IsPackageSearchBusy = true;
        PackageSearchMessage = Localize("Package.Searching");
        SetLocalizedStatus("Status.SearchingPackages");
        try
        {
            await worker.SearchPackagesAsync(PackageSearchText, IncludePrereleasePackages);
            SetLocalizedStatus("Status.Ready");
        }
        catch (Exception error)
        {
            PackageSearchMessage = error.Message;
            SetLocalizedStatus("Status.PackageSearchFailed");
        }
        finally
        {
            IsPackageSearchBusy = false;
        }
    }

    private Task InstallPackageAsync(NuGetPackageInfo? package) => package is null
        ? Task.CompletedTask
        : AddPackageAsync(package.PackageId, package.Version);

    private async Task AddPackageAsync(string packageId, string? version)
    {
        if (IsPackageSearchBusy) return;
        if (IsRunning)
        {
            PackageSearchMessage = Localize("Message.SessionBusy");
            return;
        }
        if (version is not null && packages.Any(item =>
            item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase) && item.Version == version))
        {
            PackageSearchMessage = Localize("Package.AlreadyInstalled", packageId, version);
            return;
        }

        IsPackageSearchBusy = true;
        SetLocalizedStatus("Status.RestoringPackage");
        PackageSearchMessage = Localize("Package.Installing", packageId,
            version ?? Localize("Package.LatestStable"));
        try
        {
            await worker.AddPackageAsync(packageId, version);
            SetLocalizedStatus("Status.Ready");
            var installed = packages.FirstOrDefault(item => item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase));
            PackageSearchMessage = installed == default
                ? Localize("Package.Installed", packageId)
                : Localize("Package.InstalledVersion", installed.Id, installed.Version);
        }
        catch (Exception error)
        {
            SetLocalizedStatus("Status.PackageInstallFailed");
            PackageSearchMessage = error.Message;
        }
        finally
        {
            IsPackageSearchBusy = false;
        }
    }

    private void AddPackageLibrary(PackageAddedEvent package)
    {
        for (var index = LibraryItems.Count - 1; index >= 0; index--)
            if (LibraryItems[index].Kind == "Package" &&
                LibraryItems[index].Name.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase))
                LibraryItems.RemoveAt(index);
        LibraryItems.Add(new("Package", package.PackageId, package.Version,
            $"NuGet · {package.AssemblyPaths.Count} assemblies", null));
        foreach (var path in package.AssemblyPaths) AddAssemblyLibrary(path, $"NuGet: {package.PackageId}");
    }

    private void AddAssemblyLibrary(string path, string source)
    {
        var fullPath = Path.GetFullPath(path);
        if (LibraryItems.Any(item => item.Path?.Equals(fullPath, StringComparison.OrdinalIgnoreCase) == true)) return;
        try
        {
            var identity = AssemblyName.GetAssemblyName(fullPath);
            LibraryItems.Add(new("Assembly", identity.Name ?? Path.GetFileNameWithoutExtension(fullPath),
                identity.Version?.ToString() ?? string.Empty, source, fullPath));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            LibraryItems.Add(new("Assembly", Path.GetFileNameWithoutExtension(fullPath), string.Empty, source, fullPath));
        }
    }

    private async Task RefreshTypeExplorerAsync()
    {
        typeExplorerRefresh?.Cancel();
        typeExplorerRefresh?.Dispose();
        var refresh = new CancellationTokenSource();
        typeExplorerRefresh = refresh;
        var context = Context;
        TypeExplorerStatus = Localize("Explorer.Loading");

        try
        {
            var entries = await Task.Run(
                () => languageService.GetSymbolExplorerAsync(context, refresh.Token), refresh.Token);
            if (refresh.IsCancellationRequested) return;
            typeExplorerEntries = entries;
            RebuildTypeExplorer();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (!refresh.IsCancellationRequested) TypeExplorerStatus = Localize("Explorer.Unavailable");
        }
    }

    private void ScheduleTypeExplorerRebuild()
    {
        typeExplorerSearchDelay?.Cancel();
        typeExplorerSearchDelay?.Dispose();
        typeExplorerSearchDelay = new CancellationTokenSource();
        _ = RebuildTypeExplorerAfterDelayAsync(typeExplorerSearchDelay.Token);
    }

    private async Task RebuildTypeExplorerAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(180, cancellationToken);
            RebuildTypeExplorer();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RebuildTypeExplorer()
    {
        var query = TypeExplorerSearchText.Trim();
        var allMatches = string.IsNullOrEmpty(query)
            ? typeExplorerEntries
            : typeExplorerEntries.Where(entry =>
                entry.FullName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.Kind.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.AssemblyName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.Summary.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.Parameters.Any(parameter =>
                    parameter.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    parameter.Summary.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        var totalMatchCount = allMatches.Count;
        var matched = string.IsNullOrEmpty(query)
            ? allMatches
            : allMatches.Take(MaximumExplorerSearchResults).ToArray();
        var parentKeys = matched
            .Where(static entry => entry.ContainingType is not null)
            .Select(static entry => (entry.Namespace, TypeName: entry.ContainingType!, entry.AssemblyName))
            .ToHashSet();
        var filtered = matched.Concat(typeExplorerEntries.Where(entry =>
                entry.ContainingType is null && parentKeys.Contains((entry.Namespace, entry.Name, entry.AssemblyName))))
            .Distinct()
            .ToArray();

        var root = new ExplorerNodeBuilder(string.Empty, string.Empty);
        foreach (var entry in filtered)
        {
            var namespaceName = entry.Namespace == "(session)" ? "Session" : entry.Namespace;
            var current = root;
            var fullName = string.Empty;
            foreach (var segment in namespaceName.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                fullName = string.IsNullOrEmpty(fullName) ? segment : $"{fullName}.{segment}";
                current = current.GetOrAddNamespace(segment, fullName);
            }
            current.Symbols.Add(entry);
        }

        TypeExplorerItems.Clear();
        foreach (var item in root.Namespaces.Values
                     .OrderBy(static item => item.FullName == "Session" ? 0 : 1)
                     .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase))
            TypeExplorerItems.Add(item.Build(!string.IsNullOrEmpty(query)));

        TypeExplorerStatus = string.IsNullOrEmpty(query)
            ? Localize("Explorer.Count",
                typeExplorerEntries.Count(static entry => entry.ContainingType is null && entry.Kind != "method"),
                typeExplorerEntries.Count(static entry => entry.Kind is "method" or "constructor"))
            : totalMatchCount > matched.Count
                ? Localize("Explorer.MatchesLimited", totalMatchCount, matched.Count)
                : Localize("Explorer.Matches", totalMatchCount);
    }

    private static string GetTypeGlyph(string kind) => kind switch
    {
        "class" => "C",
        "record" => "R",
        "record struct" => "R",
        "interface" => "I",
        "struct" => "S",
        "enum" => "E",
        "delegate" => "D",
        "method" => "M",
        "constructor" => "M",
        _ => "T"
    };

    private sealed class ExplorerNodeBuilder(string name, string fullName)
    {
        public string Name { get; } = name;
        public string FullName { get; } = fullName;
        public Dictionary<string, ExplorerNodeBuilder> Namespaces { get; } = new(StringComparer.Ordinal);
        public List<SymbolExplorerEntry> Symbols { get; } = [];

        public ExplorerNodeBuilder GetOrAddNamespace(string namespaceName, string namespaceFullName)
        {
            if (Namespaces.TryGetValue(namespaceName, out var existing)) return existing;
            var created = new ExplorerNodeBuilder(namespaceName, namespaceFullName);
            Namespaces.Add(namespaceName, created);
            return created;
        }

        public SymbolExplorerNode Build(bool expand)
        {
            var namespaceNodes = Namespaces.Values
                .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Build(expand));
            var membersByType = Symbols
                .Where(static item => item.ContainingType is not null)
                .ToLookup(static item => (item.ContainingType!, item.AssemblyName));
            var typeNodes = Symbols
                .Where(static item => item.ContainingType is null && item.Kind is not "method" and not "constructor")
                .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(type => CreateSymbolNode(type, membersByType[(type.Name, type.AssemblyName)]
                    .OrderBy(static method => method.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static method => method.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(static method => CreateSymbolNode(method, []))
                    .ToArray(), expand));
            var sessionMethods = Symbols
                .Where(static item => item.ContainingType is null && item.Kind is "method" or "constructor")
                .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(static item => CreateSymbolNode(item, []));
            var children = namespaceNodes.Concat(typeNodes).Concat(sessionMethods).ToArray();
            return new SymbolExplorerNode(Name, "namespace", "N", FullName, children, expand || FullName == "Session");
        }

        private static SymbolExplorerNode CreateSymbolNode(
            SymbolExplorerEntry entry,
            IReadOnlyList<SymbolExplorerNode> children,
            bool isExpanded = false) => new(
                entry.DisplayName,
                entry.Kind,
                GetTypeGlyph(entry.Kind),
                $"{entry.Signature}{Environment.NewLine}{entry.AssemblyName}",
                children,
                isExpanded,
                entry.Signature,
                entry.Summary,
                entry.Parameters.Select(static parameter =>
                    new SymbolExplorerParameter(parameter.Name, parameter.TypeName, parameter.Summary)).ToArray(),
                entry.Returns,
                entry.AssemblyName,
                CreateDocumentationPath(entry),
                entry.InheritedTypes);

        private static string? CreateDocumentationPath(SymbolExplorerEntry entry)
        {
            if (entry.AssemblyName == "Session" ||
                !entry.AssemblyName.StartsWith("System", StringComparison.Ordinal) &&
                !entry.AssemblyName.StartsWith("Microsoft", StringComparison.Ordinal)) return null;
            var fullName = entry.Kind == "constructor"
                ? string.Join('.', new[] { entry.Namespace, entry.ContainingType }.Where(static part => !string.IsNullOrEmpty(part)))
                : entry.FullName;
            return Regex.Replace(fullName, "<[^>]+>", string.Empty).ToLowerInvariant();
        }
    }

    private string FormatResultSnapshot(ResultSnapshot snapshot)
    {
        if (snapshot.Properties is not null || snapshot.Items is not null)
            return SnapshotTextFormatter.FormatCompact(snapshot);
        var preview = SnapshotTextFormatter.FormatPreview(snapshot);
        var notices = new List<string>();
        if (preview.IsLimited) notices.Add(Localize("Output.PreviewLimited"));
        if (snapshot.IsTruncated) notices.Add(Localize("Output.CaptureLimited"));
        return notices.Count == 0
            ? preview.Text
            : preview.Text + Environment.NewLine + string.Join(Environment.NewLine, notices);
    }

    private static string FormatSnapshot(ResultSnapshot snapshot)
    {
        return SnapshotTextFormatter.FormatCompact(snapshot);
    }

    private void SignalExecutionFinished()
    {
        executionCompletion?.TrySetResult();
        executionCompletion = null;
    }

    public async ValueTask DisposeAsync()
    {
        SignalExecutionFinished();
        diagnosticDelay?.Cancel();
        diagnosticDelay?.Dispose();
        typeExplorerSearchDelay?.Cancel();
        typeExplorerSearchDelay?.Dispose();
        typeExplorerRefresh?.Cancel();
        typeExplorerRefresh?.Dispose();
        await worker.DisposeAsync();
    }
}
