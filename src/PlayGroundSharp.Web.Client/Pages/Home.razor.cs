using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.Web.Client.Pages;

public partial class Home
{
    private const long MaximumUploadBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions WorkspaceJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly List<SubmissionView> history = [];
    private readonly List<string> acceptedSubmissions = [];
    private readonly List<WorkspacePackage> installedPackages = [];
    private readonly List<string> referenceNames = [];
    private IReadOnlyList<VariableInfo> variables = [];
    private IReadOnlyList<string> usings = [];
    private IReadOnlyList<NuGetPackageInfo> packageResults = [];
    private IReadOnlyList<WebCompletionItem> completions = [];
    private WebSignatureHelp? signatureHelp;
    private Guid? sessionId;
    private ElementReference editor;
    private ElementReference dropZone;
    private IJSObjectReference? module;
    private IJSObjectReference? editorBinding;
    private IJSObjectReference? dropBinding;
    private CancellationTokenSource? assistanceCancellation;
    private string code = "Enumerable.Range(1, 5)\n    .Select(x => new { x, square = x * x })\n    .ToArray()";
    private string newUsing = string.Empty;
    private string packageQuery = string.Empty;
    private string packageMessage = string.Empty;
    private string status = "接続中";
    private string? message;
    private string? completionDescription;
    private string messageKind = "info";
    private bool isBusy;
    private bool packageBusy;
    private int submissionIndex;
    private int selectedCompletion;
    private int selectedSignature;

    private int LineCount => string.IsNullOrEmpty(code) ? 1 : code.Count(character => character == '\n') + 1;
    private string StatusClass => sessionId is null ? "offline" : isBusy || packageBusy ? "busy" : "online";
    private WebCompletionItem? SelectedCompletion => completions.Count == 0 ? null : completions[selectedCompletion];

    protected override async Task OnInitializedAsync() => await EnsureSessionAsync();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        module = await JS.InvokeAsync<IJSObjectReference>("import", "./playground.js");
        editorBinding = await module.InvokeAsync<IJSObjectReference>("attachEditor", editor);
        dropBinding = await module.InvokeAsync<IJSObjectReference>("attachFileDropZone", dropZone);
        await module.InvokeVoidAsync("focusEditor", editor);
        _ = ScheduleAssistanceAsync();
    }

    private async Task HandleEditorInputAsync(ChangeEventArgs args)
    {
        code = args.Value?.ToString() ?? string.Empty;
        await ScheduleAssistanceAsync();
    }

    private async Task HandleEditorKeyDownAsync(KeyboardEventArgs args)
    {
        if (completions.Count > 0)
        {
            switch (args.Key)
            {
                case "Tab":
                    await AcceptSelectedCompletionAsync();
                    return;
                case "ArrowDown":
                    await MoveCompletionAsync(1);
                    return;
                case "ArrowUp":
                    await MoveCompletionAsync(-1);
                    return;
                case "Escape":
                    await CloseAssistanceAsync();
                    return;
            }
        }
        if (args.Key == "Enter" && !args.ShiftKey && !args.IsComposing) await ExecuteAsync();
    }

    private async Task ScheduleAssistanceAsync()
    {
        assistanceCancellation?.Cancel();
        assistanceCancellation?.Dispose();
        assistanceCancellation = new CancellationTokenSource();
        var token = assistanceCancellation.Token;
        try
        {
            await Task.Delay(180, token);
            await RefreshAssistanceAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshAssistanceAsync(CancellationToken cancellationToken)
    {
        if (module is null || string.IsNullOrWhiteSpace(code) || isBusy)
        {
            await CloseAssistanceAsync();
            return;
        }
        await EnsureSessionAsync();
        if (sessionId is null) return;
        var position = await module.InvokeAsync<int>("getCaret", cancellationToken, editor);
        var request = new WebCompletionRequest(code, position);
        using var completionResponse = await Http.PostAsJsonAsync(
            $"api/sessions/{sessionId}/completion", request, cancellationToken);
        completionResponse.EnsureSuccessStatusCode();
        var items = await completionResponse.Content.ReadFromJsonAsync<WebCompletionItem[]>(cancellationToken: cancellationToken) ?? [];
        completions = items;
        selectedCompletion = 0;
        completionDescription = null;

        using var signatureResponse = await Http.PostAsJsonAsync(
            $"api/sessions/{sessionId}/signature", request, cancellationToken);
        signatureHelp = signatureResponse.StatusCode == System.Net.HttpStatusCode.NoContent
            ? null
            : await signatureResponse.Content.ReadFromJsonAsync<WebSignatureHelp>(cancellationToken: cancellationToken);
        selectedSignature = signatureHelp?.SelectedSignature ?? 0;
        await module.InvokeVoidAsync("setCompletionState", cancellationToken, editor, completions.Count > 0);
        await InvokeAsync(StateHasChanged);
        if (completions.Count > 0) _ = LoadCompletionDescriptionAsync(cancellationToken);
    }

    private async Task LoadCompletionDescriptionAsync(CancellationToken cancellationToken = default)
    {
        var selected = SelectedCompletion;
        if (selected is null || module is null || sessionId is null) return;
        try
        {
            var position = await module.InvokeAsync<int>("getCaret", cancellationToken, editor);
            using var response = await Http.PostAsJsonAsync(
                $"api/sessions/{sessionId}/completion/description",
                new WebCompletionDescriptionRequest(code, position, selected), cancellationToken);
            if (!response.IsSuccessStatusCode) return;
            completionDescription = await response.Content.ReadFromJsonAsync<string>(cancellationToken: cancellationToken);
            await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task MoveCompletionAsync(int delta)
    {
        if (completions.Count == 0) return;
        selectedCompletion = (selectedCompletion + delta + completions.Count) % completions.Count;
        completionDescription = null;
        await LoadCompletionDescriptionAsync();
    }

    private async Task SelectAndAcceptCompletionAsync(int index)
    {
        selectedCompletion = index;
        await AcceptSelectedCompletionAsync();
    }

    private async Task AcceptSelectedCompletionAsync()
    {
        var item = SelectedCompletion;
        if (item is null || module is null) return;
        var caret = await module.InvokeAsync<int>("getCaret", editor);
        var start = item.ReplacementStart ?? FindIdentifierStart(code, caret);
        var edit = await module.InvokeAsync<WebEditorEdit>("applyCompletion", editor, start, caret, item.TextToInsert);
        code = edit.Value;
        await CloseAssistanceAsync();
        if (item.RequiredNamespace is { Length: > 0 } required && !usings.Contains(required, StringComparer.Ordinal))
            await AddUsingCoreAsync(required);
    }

    private async Task CloseAssistanceAsync()
    {
        completions = [];
        signatureHelp = null;
        completionDescription = null;
        if (module is not null) await module.InvokeVoidAsync("setCompletionState", editor, false);
    }

    private void ChangeSignature(int delta)
    {
        if (signatureHelp is not { Signatures.Count: > 0 }) return;
        selectedSignature = (selectedSignature + delta + signatureHelp.Signatures.Count) % signatureHelp.Signatures.Count;
    }

    private async Task EnsureSessionAsync()
    {
        if (sessionId is not null) return;
        try
        {
            using var response = await Http.PostAsync("api/sessions", null);
            response.EnsureSuccessStatusCode();
            var created = await response.Content.ReadFromJsonAsync<WebSessionCreated>()
                ?? throw new InvalidDataException("セッション応答が空です。");
            sessionId = created.SessionId;
            usings = created.Usings;
            status = "Worker接続済み";
        }
        catch (Exception error)
        {
            status = "接続エラー";
            ShowMessage(error.Message, "error");
        }
    }

    private async Task ExecuteAsync()
    {
        if (isBusy || string.IsNullOrWhiteSpace(code)) return;
        isBusy = true;
        status = "実行中";
        var submittedCode = code;
        try
        {
            await CloseAssistanceAsync();
            await ExecuteCodeCoreAsync(submittedCode, clearInput: true);
            status = "Worker接続済み";
        }
        catch (Exception error)
        {
            status = "再接続が必要";
            sessionId = null;
            ShowMessage($"実行に失敗しました: {error.Message}", "error");
        }
        finally
        {
            isBusy = false;
        }
    }

    private async Task ExecuteCodeCoreAsync(string submittedCode, bool clearInput)
    {
        await EnsureSessionAsync();
        if (sessionId is null) throw new InvalidOperationException("Workerに接続できません。");
        var nextIndex = submissionIndex + 1;
        var view = new SubmissionView(nextIndex, submittedCode);
        using var response = await Http.PostAsJsonAsync(
            $"api/sessions/{sessionId}/execute", new ExecuteRequest(nextIndex, submittedCode));
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? response.ReasonPhrase : detail);
        }
        var events = await response.Content.ReadFromJsonAsync<PipeEnvelope[]>() ?? [];
        ApplyEvents(view, events);
        submissionIndex = nextIndex;
        history.Add(view);
        if (view.StateAccepted) acceptedSubmissions.Add(submittedCode);
        if (clearInput) code = string.Empty;
    }

    private void ApplyEvents(SubmissionView view, IEnumerable<PipeEnvelope> events)
    {
        foreach (var envelope in events)
        {
            switch (envelope.Kind)
            {
                case MessageKinds.ConsoleOut:
                case MessageKinds.ConsoleError:
                    view.StandardOutput.Add(envelope.ReadPayload<ConsoleEvent>().Text);
                    break;
                case MessageKinds.Diagnostics:
                    view.Diagnostics.AddRange(envelope.ReadPayload<DiagnosticsEvent>().Diagnostics);
                    break;
                case MessageKinds.Result:
                    view.Result = envelope.ReadPayload<ResultEvent>().Snapshot;
                    break;
                case MessageKinds.RuntimeError:
                    view.Error = envelope.ReadPayload<RuntimeErrorEvent>().Exception;
                    break;
                case MessageKinds.Variables:
                    variables = envelope.ReadPayload<VariablesEvent>().Variables;
                    break;
                case MessageKinds.Completed:
                    view.StateAccepted = envelope.ReadPayload<ExecutionCompletedEvent>().StateAccepted;
                    break;
                case MessageKinds.SessionChanged:
                    var context = envelope.ReadPayload<SessionChangedEvent>();
                    usings = context.Usings;
                    break;
                case MessageKinds.PackageProgress:
                    packageMessage = envelope.ReadPayload<PackageProgressEvent>().Message;
                    break;
                case MessageKinds.PackageAdded:
                    var package = envelope.ReadPayload<PackageAddedEvent>();
                    installedPackages.RemoveAll(item => item.Id.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase));
                    installedPackages.Add(new(package.PackageId, package.Version));
                    packageMessage = $"{package.PackageId} {package.Version} を追加しました。";
                    break;
                case MessageKinds.Error:
                    throw new InvalidOperationException(envelope.ReadPayload<WorkerErrorEvent>().Message);
            }
        }
    }

    private async Task ResetAsync()
    {
        if (isBusy) return;
        isBusy = true;
        try
        {
            await EnsureSessionAsync();
            if (sessionId is null) return;
            var events = await PostAndReadEventsAsync<object?>($"api/sessions/{sessionId}/reset", null);
            ApplyEvents(new SubmissionView(0, string.Empty), events);
            variables = [];
            history.Clear();
            acceptedSubmissions.Clear();
            submissionIndex = 0;
            status = "Worker接続済み";
            ShowMessage("セッションをリセットしました。", "success");
        }
        catch (Exception error)
        {
            ShowMessage(error.Message, "error");
        }
        finally
        {
            isBusy = false;
        }
    }

    private async Task AddUsingAsync()
    {
        var candidate = newUsing.Trim();
        if (isBusy || candidate.Length == 0) return;
        isBusy = true;
        try
        {
            await AddUsingCoreAsync(candidate);
            newUsing = string.Empty;
            ShowMessage($"using {candidate} を追加しました。", "success");
        }
        catch (Exception error)
        {
            ShowMessage(error.Message, "error");
        }
        finally
        {
            isBusy = false;
        }
    }

    private async Task AddUsingCoreAsync(string candidate)
    {
        await EnsureSessionAsync();
        if (sessionId is null) throw new InvalidOperationException("Workerに接続できません。");
        var events = await PostAndReadEventsAsync($"api/sessions/{sessionId}/usings/add", new WebUsingRequest(candidate));
        ApplyEvents(new SubmissionView(0, string.Empty), events);
    }

    private async Task RemoveUsingAsync(string candidate)
    {
        if (isBusy) return;
        isBusy = true;
        try
        {
            var events = await PostAndReadEventsAsync($"api/sessions/{sessionId}/usings/remove", new WebUsingRequest(candidate));
            ApplyEvents(new SubmissionView(0, string.Empty), events);
            variables = [];
            history.Clear();
            acceptedSubmissions.Clear();
            submissionIndex = 0;
            ShowMessage($"using {candidate} を削除し、実行状態をリセットしました。", "success");
        }
        catch (Exception error)
        {
            ShowMessage(error.Message, "error");
        }
        finally
        {
            isBusy = false;
        }
    }

    private async Task SearchPackagesAsync()
    {
        if (packageBusy || string.IsNullOrWhiteSpace(packageQuery)) return;
        packageBusy = true;
        packageMessage = "検索中…";
        try
        {
            await EnsureSessionAsync();
            var events = await PostAndReadEventsAsync(
                $"api/sessions/{sessionId}/packages/search", new SearchPackagesRequest(packageQuery.Trim(), false, 12));
            var result = events.FirstOrDefault(item => item.Kind == MessageKinds.PackageSearchResults)
                ?.ReadPayload<PackageSearchResultsEvent>();
            packageResults = result?.Packages ?? [];
            packageMessage = result is null ? "検索結果を取得できませんでした。" : $"{result.TotalHits:N0}件中 {result.Packages.Count}件を表示";
        }
        catch (Exception error)
        {
            packageMessage = error.Message;
        }
        finally
        {
            packageBusy = false;
        }
    }

    private async Task InstallPackageAsync(NuGetPackageInfo package)
    {
        if (packageBusy) return;
        packageBusy = true;
        try
        {
            await InstallPackageCoreAsync(package.PackageId, package.Version);
        }
        catch (Exception error)
        {
            packageMessage = error.Message;
        }
        finally
        {
            packageBusy = false;
        }
    }

    private async Task InstallPackageCoreAsync(string packageId, string version)
    {
        packageMessage = $"{packageId} {version} を復元中…";
        var events = await PostAndReadEventsAsync(
            $"api/sessions/{sessionId}/packages/add", new AddPackageRequest(packageId, version));
        ApplyEvents(new SubmissionView(0, string.Empty), events);
    }

    private async Task HandleFileSelectedAsync(InputFileChangeEventArgs args)
    {
        var file = args.File;
        var extension = Path.GetExtension(file.Name);
        if (extension.Equals(".pgsworkspace", StringComparison.OrdinalIgnoreCase))
        {
            await LoadWorkspaceAsync(file);
            return;
        }
        await UploadFileAsync(file);
    }

    private async Task UploadFileAsync(IBrowserFile file)
    {
        if (isBusy) return;
        isBusy = true;
        try
        {
            await EnsureSessionAsync();
            using var form = new MultipartFormDataContent();
            await using var stream = file.OpenReadStream(MaximumUploadBytes);
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType.Length == 0 ? "application/octet-stream" : file.ContentType);
            form.Add(content, "file", file.Name);
            using var response = await Http.PostAsync($"api/sessions/{sessionId}/files", form);
            response.EnsureSuccessStatusCode();
            var uploaded = await response.Content.ReadFromJsonAsync<WebFileUploaded>()
                ?? throw new InvalidDataException("アップロード応答が空です。");
            ApplyEvents(new SubmissionView(0, string.Empty), uploaded.Events);
            if (Path.GetExtension(file.Name).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                if (!referenceNames.Contains(file.Name, StringComparer.OrdinalIgnoreCase)) referenceNames.Add(file.Name);
                ShowMessage($"{file.Name} を参照へ追加しました。", "success");
            }
            else
            {
                code = CreateDataSnippet(uploaded.ServerPath, file.Name);
                if (module is not null) await module.InvokeVoidAsync("focusEditor", editor);
                ShowMessage($"{file.Name} をアップロードし、読込コードを挿入しました。", "success");
                await ScheduleAssistanceAsync();
            }
        }
        catch (Exception error)
        {
            ShowMessage(error.Message, "error");
        }
        finally
        {
            isBusy = false;
        }
    }

    private async Task SaveWorkspaceAsync()
    {
        if (module is null) return;
        var document = new WorkspaceDocument(
            WorkspaceDocument.CurrentVersion,
            DateTime.UtcNow,
            [.. acceptedSubmissions],
            [.. usings],
            [],
            [.. installedPackages],
            code);
        var json = JsonSerializer.Serialize(document, WorkspaceJson);
        await module.InvokeVoidAsync("downloadText", $"PlayGroundSharp-{DateTime.Now:yyyyMMdd-HHmmss}.pgsworkspace", json);
        ShowMessage(referenceNames.Count == 0
            ? "ワークスペースを保存しました。"
            : "ワークスペースを保存しました。アップロード済みDLL本体は含まれません。", "success");
    }

    private async Task LoadWorkspaceAsync(IBrowserFile file)
    {
        if (isBusy) return;
        isBusy = true;
        try
        {
            await using var stream = file.OpenReadStream(WorkspaceFile.MaximumFileBytes);
            var document = await JsonSerializer.DeserializeAsync<WorkspaceDocument>(stream, WorkspaceJson)
                ?? throw new InvalidDataException("ワークスペースが空です。");
            if (document.Version != WorkspaceDocument.CurrentVersion)
                throw new InvalidDataException($"未対応のワークスペースバージョンです: {document.Version}");

            await StartFreshSessionAsync();
            foreach (var import in usings.Except(document.Imports, StringComparer.Ordinal).ToArray())
            {
                var events = await PostAndReadEventsAsync(
                    $"api/sessions/{sessionId}/usings/remove", new WebUsingRequest(import));
                ApplyEvents(new SubmissionView(0, string.Empty), events);
            }
            foreach (var import in document.Imports.Except(usings, StringComparer.Ordinal)) await AddUsingCoreAsync(import);
            foreach (var package in document.Packages) await InstallPackageCoreAsync(package.Id, package.Version);
            foreach (var submission in document.Submissions) await ExecuteCodeCoreAsync(submission, clearInput: false);
            code = document.InputText;
            status = "Worker接続済み";
            ShowMessage(document.References.Count == 0
                ? "ワークスペースを復元しました。"
                : "ワークスペースを復元しました。デスクトップのDLLパスはWeb版ではスキップしました。", "success");
        }
        catch (Exception error)
        {
            ShowMessage($"ワークスペースを開けませんでした: {error.Message}", "error");
        }
        finally
        {
            isBusy = false;
        }
    }

    private async Task StartFreshSessionAsync()
    {
        if (sessionId is { } current)
        {
            try { await Http.DeleteAsync($"api/sessions/{current}"); } catch (HttpRequestException) { }
        }
        sessionId = null;
        history.Clear();
        acceptedSubmissions.Clear();
        installedPackages.Clear();
        referenceNames.Clear();
        variables = [];
        submissionIndex = 0;
        await EnsureSessionAsync();
    }

    private async Task<PipeEnvelope[]> PostAndReadEventsAsync<T>(string uri, T payload)
    {
        using var response = payload is null
            ? await Http.PostAsync(uri, null)
            : await Http.PostAsJsonAsync(uri, payload);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<PipeEnvelope[]>() ?? [];
    }

    private async Task CopyAsync(SubmissionView item)
    {
        if (module is null) return;
        var result = item.Result?.Display ?? item.Error?.Message ?? string.Concat(item.StandardOutput);
        await module.InvokeVoidAsync("copyText", $"> {item.Code}\n{result}");
        ShowMessage("クリップボードへコピーしました。", "success");
    }

    private void ClearHistory() => history.Clear();

    private void ShowMessage(string text, string kind)
    {
        message = text;
        messageKind = kind;
    }

    private static int FindIdentifierStart(string value, int position)
    {
        var index = Math.Clamp(position, 0, value.Length);
        while (index > 0 && (char.IsLetterOrDigit(value[index - 1]) || value[index - 1] == '_')) index--;
        return index;
    }

    private static string CompletionIcon(WebCompletionItem item) => item.Tags.FirstOrDefault() switch
    {
        "Class" => "C",
        "Interface" => "I",
        "Method" => "M",
        "Property" => "P",
        "Keyword" => "K",
        _ => "·"
    };

    private static string CreateDataSnippet(string path, string fileName)
    {
        var literal = DataSnippetBuilder.ToVerbatimStringLiteral(path);
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".json" => $"await Data.ReadJsonAsync({literal})",
            ".jsonl" or ".ndjson" => DataSnippetBuilder.CreateJsonLines(path),
            _ => $"Data.PreviewText({literal}, 65536)"
        };
    }

    private static string DisplayType(ResultSnapshot snapshot) => snapshot.Kind switch
    {
        SnapshotKind.Sequence when snapshot.TypeName?.EndsWith("[]", StringComparison.Ordinal) == true =>
            $"{ShortTypeName(snapshot.TypeName[..^2])}[]",
        SnapshotKind.Sequence => "IEnumerable",
        SnapshotKind.Json => "JSON",
        _ => ShortTypeName(snapshot.TypeName)
    };

    private static string ShortTypeName(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return "value";
        var genericMarker = typeName.IndexOf('`');
        var trimmed = genericMarker >= 0 ? typeName[..genericMarker] : typeName;
        var assemblyMarker = trimmed.IndexOf(',');
        if (assemblyMarker >= 0) trimmed = trimmed[..assemblyMarker];
        if (trimmed.Contains("AnonymousType", StringComparison.Ordinal)) return "anonymous";
        var separator = Math.Max(trimmed.LastIndexOf('.'), trimmed.LastIndexOf('+'));
        return separator >= 0 ? trimmed[(separator + 1)..] : trimmed;
    }

    public async ValueTask DisposeAsync()
    {
        assistanceCancellation?.Cancel();
        assistanceCancellation?.Dispose();
        if (sessionId is { } current)
        {
            try { await Http.DeleteAsync($"api/sessions/{current}"); } catch (HttpRequestException) { }
        }
        if (editorBinding is not null) await editorBinding.InvokeVoidAsync("dispose");
        if (dropBinding is not null) await dropBinding.InvokeVoidAsync("dispose");
        if (module is not null) await module.DisposeAsync();
    }

    private sealed class SubmissionView(int index, string code)
    {
        public int Index { get; } = index;
        public string Code { get; } = code;
        public List<string> StandardOutput { get; } = [];
        public List<DiagnosticInfo> Diagnostics { get; } = [];
        public ResultSnapshot? Result { get; set; }
        public ExceptionInfo? Error { get; set; }
        public bool StateAccepted { get; set; }
    }
}
