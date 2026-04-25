using SplunkOpsRca.Domain.Models;

namespace SplunkOpsRca.Application.Abstractions;

public interface ILogParser
{
    Task<IReadOnlyList<LogRecord>> ParseAsync(Stream stream, CancellationToken cancellationToken);
}
