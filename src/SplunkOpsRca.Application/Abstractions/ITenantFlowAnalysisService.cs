using SplunkOpsRca.Domain.Models;

namespace SplunkOpsRca.Application.Abstractions;

public interface ITenantFlowAnalysisService
{
    TenantClientFlowAnalysis Analyze(IReadOnlyList<LogRecord> records);
}
