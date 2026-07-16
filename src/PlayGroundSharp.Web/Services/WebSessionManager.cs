using System.Collections.Concurrent;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.Web.Services;

public sealed class WebSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, WorkerProcessSession> sessions = new();

    public async Task<(Guid SessionId, IReadOnlyList<string> Usings)> CreateAsync(CancellationToken cancellationToken)
    {
        var session = new WorkerProcessSession();
        await session.StartAsync(cancellationToken);
        var sessionId = Guid.NewGuid();
        if (!sessions.TryAdd(sessionId, session))
        {
            await session.DisposeAsync();
            throw new InvalidOperationException("Could not register the Worker session.");
        }
        return (sessionId, session.Context.Imports);
    }

    public bool TryGet(Guid sessionId, out WorkerProcessSession session) =>
        sessions.TryGetValue(sessionId, out session!);

    public async Task RemoveAsync(Guid sessionId)
    {
        if (sessions.TryRemove(sessionId, out var session)) await session.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        var active = sessions.ToArray();
        sessions.Clear();
        foreach (var (_, session) in active) await session.DisposeAsync();
    }
}
