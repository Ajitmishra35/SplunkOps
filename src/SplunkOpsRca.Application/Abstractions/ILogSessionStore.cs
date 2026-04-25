using SplunkOpsRca.Domain.Models;

namespace SplunkOpsRca.Application.Abstractions;

public interface ILogSessionStore
{
    Task SaveAsync(LogSession session, CancellationToken cancellationToken);
    Task<LogSession?> GetAsync(string sessionId, CancellationToken cancellationToken);
}
