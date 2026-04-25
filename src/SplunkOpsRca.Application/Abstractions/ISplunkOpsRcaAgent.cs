using SplunkOpsRca.Domain.Models;

namespace SplunkOpsRca.Application.Abstractions;

public interface ISplunkOpsRcaAgent
{
    Task<AgentResponse> AskAsync(LogAnalysisRequest request, LogAnalysisResult deterministicAnalysis, IReadOnlyList<LogRecord> records, CancellationToken cancellationToken);
}
