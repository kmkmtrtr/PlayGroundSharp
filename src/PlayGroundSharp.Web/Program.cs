using System.Net;
using PlayGroundSharp.Core;
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

app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(PlayGroundSharp.Web.Client._Imports).Assembly);

await app.RunAsync();
