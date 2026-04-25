using SplunkOpsRca.Domain.Models;

namespace SplunkOpsRca.Application.Abstractions;

public interface ILogAnalysisService
{
    LogAnalysisResult Analyze(string sessionId, IReadOnlyList<LogRecord> records, string? question = null, string? action = null);
    IReadOnlyList<DetectedFieldSummary> DetectFields(IReadOnlyList<LogRecord> records);
    CorrelationTrace TraceByCorrelationId(IReadOnlyList<LogRecord> records, string correlationId);
}
