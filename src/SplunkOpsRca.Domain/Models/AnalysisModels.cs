namespace SplunkOpsRca.Domain.Models;

public sealed record LogAnalysisRequest(
    string SessionId,
    string? Question,
    string? Action,
    int MaxEvidenceItems = 20);

public sealed record LogAnalysisResult
{
    public string SessionId { get; init; } = "";
    public int RecordCount { get; init; }
    public IReadOnlyList<DetectedFieldSummary> DetectedFields { get; init; } = [];
    public IReadOnlyList<ErrorGroup> ErrorsByService { get; init; } = [];
    public IReadOnlyList<ErrorGroup> ErrorsByPod { get; init; } = [];
    public IReadOnlyList<ErrorGroup> ErrorsByException { get; init; } = [];
    public IReadOnlyList<ErrorGroup> ErrorsByCorrelationId { get; init; } = [];
    public IReadOnlyList<ErrorGroup> ErrorsByApiPath { get; init; } = [];
    public IReadOnlyList<ErrorGroup> ErrorsByHttpStatusCode { get; init; } = [];
    public IReadOnlyList<string> DetectedPatterns { get; init; } = [];
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public TenantClientFlowAnalysis TenantClientFlowAnalysis { get; init; } = new();
    public RootCauseClassification RootCause { get; init; } = RootCauseClassification.Unknown;
    public string Confidence { get; init; } = "Low";
    public AgentResponse AgentResponse { get; init; } = new();
}

public sealed record DetectedFieldSummary(string Name, int Count, IReadOnlyList<string> SampleValues);

public sealed record ErrorGroup(string Key, int Count, string? FirstSeen, string? LastSeen, IReadOnlyList<string> Examples);

public sealed record ServiceImpactSummary(string Service, int ErrorCount, IReadOnlyList<string> Pods, IReadOnlyList<string> ApiPaths);

public sealed record PodImpactSummary(string Pod, string Namespace, int ErrorCount, IReadOnlyList<string> Services);

public sealed record CorrelationTrace(string CorrelationId, int RecordCount, IReadOnlyList<LogRecord> Records);

public sealed record IncidentSummary(
    string Title,
    string Severity,
    string BusinessImpact,
    string TechnicalImpact,
    string ProbableRootCause,
    IReadOnlyList<string> ImmediateActions,
    IReadOnlyList<string> DeveloperActions);

public sealed record TenantClientFlowAnalysis
{
    public int TotalTenants { get; init; }
    public int TotalClients { get; init; }
    public int TotalCorrelations { get; init; }
    public int DeviatingCorrelationCount { get; init; }
    public int InvalidDeviationCount { get; init; }
    public int ExpectedDeviationCount { get; init; }
    public int NeedsReviewDeviationCount { get; init; }
    public IReadOnlyList<NormalFlowSignature> LearnedNormalFlows { get; init; } = [];
    public IReadOnlyList<TenantClientFlowSummary> TenantClientSummaries { get; init; } = [];
    public IReadOnlyList<FlowDeviation> Deviations { get; init; } = [];
    public IReadOnlyList<string> DecisionNotes { get; init; } = [];
}

public sealed record NormalFlowSignature(
    string TenantKey,
    string ClientKey,
    string ProcessKey,
    string Signature,
    int SuccessfulCorrelationCount,
    double MedianDurationMs);

public sealed record TenantClientFlowSummary(
    string TenantKey,
    string ClientKey,
    int RecordCount,
    int CorrelationCount,
    int ErrorCount,
    int DeviationCount,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Processes,
    IReadOnlyList<string> NormalFlowSignatures,
    IReadOnlyList<string> ObservedFlowSignatures,
    string Decision);

public sealed record FlowDeviation(
    string TenantKey,
    string ClientKey,
    string ProcessKey,
    string CorrelationId,
    string DeviationType,
    string ExpectedFlow,
    string ObservedFlow,
    string Validity,
    string Severity,
    string Confidence,
    IReadOnlyList<string> Evidence,
    string RecommendedAction);

public enum RootCauseClassification
{
    ApplicationCodeIssue,
    ConfigurationIssue,
    DatabaseIssue,
    ExternalDependencyIssue,
    KubernetesInfrastructureIssue,
    AuthenticationAuthorizationIssue,
    NetworkTimeoutIssue,
    DeploymentVersionCompatibilityIssue,
    DataIssue,
    Unknown
}

public sealed record AgentResponse
{
    public string ExecutiveSummary { get; init; } = "";
    public string TechnicalSummary { get; init; } = "";
    public IReadOnlyList<string> ImpactedComponents { get; init; } = [];
    public string ErrorPattern { get; init; } = "";
    public string ProbableRootCause { get; init; } = "";
    public IReadOnlyList<string> EvidenceFromLogs { get; init; } = [];
    public IReadOnlyList<string> OpsActions { get; init; } = [];
    public IReadOnlyList<string> DeveloperActions { get; init; } = [];
    public IReadOnlyList<string> ImmediateMitigation { get; init; } = [];
    public IReadOnlyList<string> DeveloperFix { get; init; } = [];
    public IReadOnlyList<string> LongTermPrevention { get; init; } = [];
    public IReadOnlyList<string> FollowUpQueries { get; init; } = [];
    public string Markdown { get; init; } = "";
}
