using SplunkOpsRca.Application.Abstractions;
using SplunkOpsRca.Domain.Models;

namespace SplunkOpsRca.Application.UseCases;

public sealed class LogWorkflowService(
    ILogParser parser,
    ILogAnalysisService analysisService,
    ILogSessionStore sessionStore,
    ISplunkOpsRcaAgent agent)
{
    public const long MaxUploadBytes = 50 * 1024 * 1024;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".json", ".log", ".ndjson", ".txt" };

    public async Task<UploadResult> UploadAsync(string fileName, long length, Stream stream, CancellationToken cancellationToken)
    {
        var errors = ValidateFile(fileName, length);
        if (errors.Count > 0)
        {
            return new UploadResult { FileName = fileName, ValidationErrors = errors };
        }

        var records = await parser.ParseAsync(stream, cancellationToken);
        var sessionId = $"log_{Guid.NewGuid():N}";
        var fields = analysisService.DetectFields(records);
        await sessionStore.SaveAsync(new LogSession
        {
            SessionId = sessionId,
            FileName = fileName,
            Records = records,
            DetectedFields = fields
        }, cancellationToken);

        return new UploadResult
        {
            SessionId = sessionId,
            FileName = fileName,
            RecordCount = records.Count,
            DetectedFields = fields
        };
    }

    public async Task<LogAnalysisResult?> AnalyzeAsync(LogAnalysisRequest request, CancellationToken cancellationToken)
    {
        var session = await sessionStore.GetAsync(request.SessionId, cancellationToken);
        if (session is null)
        {
            return null;
        }

        var deterministic = analysisService.Analyze(request.SessionId, session.Records, request.Question, request.Action);
        var agentResponse = await agent.AskAsync(request, deterministic, session.Records, cancellationToken);
        return deterministic with { AgentResponse = agentResponse };
    }

    public async Task<LogSession?> GetSummaryAsync(string sessionId, CancellationToken cancellationToken) =>
        await sessionStore.GetAsync(sessionId, cancellationToken);

    public async Task<IReadOnlyList<ErrorGroup>?> GetErrorsAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await sessionStore.GetAsync(sessionId, cancellationToken);
        return session is null ? null : analysisService.Analyze(sessionId, session.Records).ErrorsByService;
    }

    public async Task<CorrelationTrace?> GetCorrelationTraceAsync(string sessionId, string correlationId, CancellationToken cancellationToken)
    {
        var session = await sessionStore.GetAsync(sessionId, cancellationToken);
        return session is null ? null : analysisService.TraceByCorrelationId(session.Records, correlationId);
    }

    private static List<string> ValidateFile(string fileName, long length)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            errors.Add("A file name is required.");
        }

        if (!AllowedExtensions.Contains(Path.GetExtension(fileName)))
        {
            errors.Add("Allowed file extensions are .json, .ndjson, .log, and .txt.");
        }

        if (length <= 0)
        {
            errors.Add("The uploaded file is empty.");
        }

        if (length > MaxUploadBytes)
        {
            errors.Add($"File size exceeds the {MaxUploadBytes / 1024 / 1024} MB limit.");
        }

        return errors;
    }
}
