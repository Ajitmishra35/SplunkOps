namespace SplunkOpsRca.Domain.Models;

public sealed record UploadResult
{
    public string SessionId { get; init; } = "";
    public string FileName { get; init; } = "";
    public int RecordCount { get; init; }
    public IReadOnlyList<DetectedFieldSummary> DetectedFields { get; init; } = [];
    public IReadOnlyList<string> ValidationErrors { get; init; } = [];
}

public sealed record LogSession
{
    public string SessionId { get; init; } = "";
    public string FileName { get; init; } = "";
    public DateTimeOffset UploadedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<LogRecord> Records { get; init; } = [];
    public IReadOnlyList<DetectedFieldSummary> DetectedFields { get; init; } = [];
}
