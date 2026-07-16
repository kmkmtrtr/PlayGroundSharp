using System.Net;
using PlayGroundSharp.Core;
using PlayGroundSharp.LanguageService;
using PlayGroundSharp.Web.Client;
using PlayGroundSharp.Web.Components;
using PlayGroundSharp.Web.Services;
using PlayGroundSharp.Worker;

if (args is ["--web-worker", "--pipe", var pipeName])
{
    await new WorkerHost(pipeName).RunAsync();
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveWebAssemblyComponents();
builder.Services.AddSingleton<WebSessionManager>();
builder.Services.AddSingleton<CSharpLanguageService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") &&
        context.Connection.RemoteIpAddress is { } address && !IPAddress.IsLoopback(address))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Wasm preview API is available only from this computer.");
        return;
    }
    await next();
});

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();

app.MapPost("/api/sessions", async (WebSessionManager sessions, CancellationToken cancellationToken) =>
{
    var (sessionId, usings) = await sessions.CreateAsync(cancellationToken);
    return Results.Ok(new WebSessionCreated(sessionId, usings));
});

app.MapPost("/api/sessions/{sessionId:guid}/execute", async (
    Guid sessionId, ExecuteRequest request, WebSessionManager sessions, CancellationToken cancellationToken) =>
{
    if (!sessions.TryGet(sessionId, out var session)) return Results.NotFound("Session was not found.");
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(15));
    try
    {
        return Results.Ok(await session.ExecuteAsync(request, timeout.Token));
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        await sessions.RemoveAsync(sessionId);
        return Results.Json(new { error = "Execution exceeded 15 seconds. The Worker was stopped and the session was discarded." }, statusCode: 408);
    }
});

app.MapPost("/api/sessions/{sessionId:guid}/reset", async (
    Guid sessionId, WebSessionManager sessions, CancellationToken cancellationToken) =>
{
    return sessions.TryGet(sessionId, out var session)
        ? Results.Ok(await session.ResetAsync(cancellationToken))
        : Results.NotFound("Session was not found.");
});

app.MapPost("/api/sessions/{sessionId:guid}/usings/add", async (
    Guid sessionId, WebUsingRequest request, WebSessionManager sessions, CancellationToken cancellationToken) =>
{
    return sessions.TryGet(sessionId, out var session)
        ? Results.Ok(await session.AddUsingAsync(request.Namespace, cancellationToken))
        : Results.NotFound("Session was not found.");
});

app.MapPost("/api/sessions/{sessionId:guid}/usings/remove", async (
    Guid sessionId, WebUsingRequest request, WebSessionManager sessions, CancellationToken cancellationToken) =>
{
    if (!sessions.TryGet(sessionId, out var session)) return Results.NotFound("Session was not found.");
    await session.ResetAsync(cancellationToken);
    return Results.Ok(await session.RemoveUsingAsync(request.Namespace, cancellationToken));
});

app.MapPost("/api/sessions/{sessionId:guid}/packages/search", async (
    Guid sessionId, SearchPackagesRequest request, WebSessionManager sessions, CancellationToken cancellationToken) =>
{
    return sessions.TryGet(sessionId, out var session)
        ? Results.Ok(await session.SearchPackagesAsync(request, cancellationToken))
        : Results.NotFound("Session was not found.");
});

app.MapPost("/api/sessions/{sessionId:guid}/packages/add", async (
    Guid sessionId, AddPackageRequest request, WebSessionManager sessions, CancellationToken cancellationToken) =>
{
    return sessions.TryGet(sessionId, out var session)
        ? Results.Ok(await session.AddPackageAsync(request, cancellationToken))
        : Results.NotFound("Session was not found.");
});

app.MapPost("/api/sessions/{sessionId:guid}/files", async (
    Guid sessionId, HttpRequest request, WebSessionManager sessions, CancellationToken cancellationToken) =>
{
    if (!sessions.TryGet(sessionId, out var session)) return Results.NotFound("Session was not found.");
    if (!request.HasFormContentType) return Results.BadRequest("A multipart file is required.");
    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0) return Results.BadRequest("The uploaded file is empty.");
    if (file.Length > 64L * 1024 * 1024) return Results.BadRequest("The uploaded file exceeds 64 MiB.");
    await using var stream = file.OpenReadStream();
    var path = await session.SaveFileAsync(file.FileName, stream, cancellationToken);
    IReadOnlyList<PipeEnvelope> events = [];
    if (Path.GetExtension(file.FileName).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        events = await session.AddReferenceAsync(path, cancellationToken);
    return Results.Ok(new WebFileUploaded(Path.GetFileName(file.FileName), path, events));
}).DisableAntiforgery();

app.MapPost("/api/sessions/{sessionId:guid}/completion", async (
    Guid sessionId, WebCompletionRequest request, WebSessionManager sessions,
    CSharpLanguageService languageService, CancellationToken cancellationToken) =>
{
    if (!sessions.TryGet(sessionId, out var session)) return Results.NotFound("Session was not found.");
    var items = await languageService.GetCompletionsAsync(session.Context, request.Code, request.Position, cancellationToken);
    return Results.Ok(FilterCompletions(items, request.Code, request.Position).Take(150).Select(static item => new WebCompletionItem(
        item.DisplayText, item.FilterText, item.SortText, item.Tags, item.TextToInsert,
        item.RequiredNamespace, item.ReplacementStart, item.NamespaceHint, item.IsExtensionMethod)));
});

app.MapPost("/api/sessions/{sessionId:guid}/completion/description", async (
    Guid sessionId, WebCompletionDescriptionRequest request, WebSessionManager sessions,
    CSharpLanguageService languageService, CancellationToken cancellationToken) =>
{
    if (!sessions.TryGet(sessionId, out var session)) return Results.NotFound("Session was not found.");
    var item = request.Item;
    var candidate = new CompletionCandidate(item.DisplayText, item.FilterText, item.SortText, item.Tags,
        item.TextToInsert, item.RequiredNamespace, item.ReplacementStart, item.NamespaceHint);
    var description = await languageService.GetCompletionDescriptionAsync(
        session.Context, request.Code, request.Position, candidate, cancellationToken);
    return Results.Ok(description);
});

app.MapPost("/api/sessions/{sessionId:guid}/signature", async (
    Guid sessionId, WebCompletionRequest request, WebSessionManager sessions,
    CSharpLanguageService languageService, CancellationToken cancellationToken) =>
{
    if (!sessions.TryGet(sessionId, out var session)) return Results.NotFound("Session was not found.");
    var help = await languageService.GetSignatureHelpAsync(session.Context, request.Code, request.Position, cancellationToken);
    return help is null
        ? Results.NoContent()
        : Results.Ok(new WebSignatureHelp(help.Signatures.Select(static signature => new WebSignature(
            signature.DisplayText, signature.Summary,
            signature.Parameters.Select(static parameter => new WebSignatureParameter(
                parameter.Name, parameter.TypeName, parameter.Summary)).ToArray(),
            signature.ActiveParameter)).ToArray(), help.SelectedSignature));
});

app.MapDelete("/api/sessions/{sessionId:guid}", async (Guid sessionId, WebSessionManager sessions) =>
{
    await sessions.RemoveAsync(sessionId);
    return Results.NoContent();
});

app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(PlayGroundSharp.Web.Client._Imports).Assembly);

await app.RunAsync();

static IEnumerable<CompletionCandidate> FilterCompletions(
    IEnumerable<CompletionCandidate> items,
    string code,
    int position)
{
    var caret = Math.Clamp(position, 0, code.Length);
    var start = caret;
    while (start > 0 && (char.IsLetterOrDigit(code[start - 1]) || code[start - 1] == '_')) start--;
    var prefix = code[start..caret];
    return prefix.Length == 0
        ? items
        : items.Where(item => item.FilterText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
