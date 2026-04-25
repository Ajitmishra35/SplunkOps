using System.Collections.Concurrent;
using SplunkOpsRca.Application.Abstractions;
using SplunkOpsRca.Domain.Models;

namespace SplunkOpsRca.Infrastructure.Storage;

public sealed class InMemoryLogSessionStore : ILogSessionStore
{
    private readonly ConcurrentDictionary<string, LogSession> _sessions = new();

    // Production replacement options:
    // - Redis for short-lived active sessions
    // - Azure Blob Storage for masked upload payloads
    // - Cosmos DB or SQL Server for metadata, audit, and incident history
    // Keep this behind ILogSessionStore so the API and UI do not change.
    public Task SaveAsync(LogSession session, CancellationToken cancellationToken)
    {
        _sessions[session.SessionId] = session;
        return Task.CompletedTask;
    }

    public Task<LogSession?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }
}
