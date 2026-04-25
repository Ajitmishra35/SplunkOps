namespace SplunkOpsRca.Domain.Models;

public sealed record LogRecord
{
    public DateTimeOffset? Timestamp { get; init; }
    public string? RawTime { get; init; }
    public string? Level { get; init; }
    public string? Severity { get; init; }
    public string? Service { get; init; }
    public string? ServiceName { get; init; }
    public string? Pod { get; init; }
    public string? Namespace { get; init; }
    public string? Container { get; init; }
    public string? Host { get; init; }
    public string? Source { get; init; }
    public string? SourceType { get; init; }
    public string? Index { get; init; }
    public string? Message { get; init; }
    public string? Log { get; init; }
    public string? Exception { get; init; }
    public string? StackTrace { get; init; }
    public string? TraceId { get; init; }
    public string? SpanId { get; init; }
    public string? CorrelationId { get; init; }
    public string? RequestId { get; init; }
    public string? TenantId { get; init; }
    public string? OrganizationId { get; init; }
    public string? UserId { get; init; }
    public string? Path { get; init; }
    public string? Method { get; init; }
    public int? HttpStatusCode { get; init; }
    public double? DurationMs { get; init; }
    public string? ExceptionType { get; init; }
    public string MaskedRaw { get; init; } = "";
    public IReadOnlyDictionary<string, string?> Fields { get; init; } = new Dictionary<string, string?>();

    public string ServiceKey => FirstNonEmpty(Service, ServiceName, Fields.GetValueOrDefault("app"), "unknown-service");
    public string PodKey => FirstNonEmpty(Pod, Fields.GetValueOrDefault("pod_name"), Fields.GetValueOrDefault("kubernetes.pod_name"), "unknown-pod");
    public string NamespaceKey => FirstNonEmpty(Namespace, Fields.GetValueOrDefault("kubernetes.namespace_name"), "unknown-namespace");
    public string LevelKey => FirstNonEmpty(Level, Severity, "unknown");
    public string ApiPathKey => FirstNonEmpty(Path, "unknown-path");
    public string CorrelationKey => FirstNonEmpty(CorrelationId, TraceId, RequestId, "unknown-correlation");
    public string ExceptionKey => FirstNonEmpty(ExceptionType, Exception, "unknown-exception");

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "unknown";
}
