using System.Text;
using SplunkOpsRca.Application.Abstractions;
using SplunkOpsRca.Domain.Models;

namespace SplunkOpsRca.Infrastructure.Services;

public sealed class LogAnalysisService(ITenantFlowAnalysisService? tenantFlowAnalysisService = null) : ILogAnalysisService
{
    private readonly ITenantFlowAnalysisService tenantFlowAnalysisService = tenantFlowAnalysisService ?? new TenantFlowAnalysisService();

    public LogAnalysisResult Analyze(string sessionId, IReadOnlyList<LogRecord> records, string? question = null, string? action = null)
    {
        var errorRecords = records.Where(IsErrorLike).ToArray();
        var patterns = DetectPatterns(records, errorRecords);
        var rootCause = Classify(patterns, errorRecords);
        var confidence = CalculateConfidence(rootCause, errorRecords, patterns);
        var evidence = errorRecords.Take(20).Select(ToEvidence).ToArray();
        var tenantFlowAnalysis = AnalyzeTenantClientFlows(records);
        var agentResponse = BuildDeterministicResponse(records, errorRecords, patterns, rootCause, confidence, evidence, tenantFlowAnalysis, question, action);

        return new LogAnalysisResult
        {
            SessionId = sessionId,
            RecordCount = records.Count,
            DetectedFields = DetectFields(records),
            ErrorsByService = Group(errorRecords, record => record.ServiceKey),
            ErrorsByPod = Group(errorRecords, record => record.PodKey),
            ErrorsByException = Group(errorRecords, record => record.ExceptionKey),
            ErrorsByCorrelationId = Group(errorRecords.Where(record => record.CorrelationKey != "unknown-correlation"), record => record.CorrelationKey),
            ErrorsByApiPath = Group(errorRecords.Where(record => record.ApiPathKey != "unknown-path"), record => record.ApiPathKey),
            ErrorsByHttpStatusCode = Group(errorRecords.Where(record => record.HttpStatusCode.HasValue), record => record.HttpStatusCode!.Value.ToString()),
            DetectedPatterns = patterns,
            Evidence = evidence,
            TenantClientFlowAnalysis = tenantFlowAnalysis,
            RootCause = rootCause,
            Confidence = confidence,
            AgentResponse = agentResponse
        };
    }

    public IReadOnlyList<DetectedFieldSummary> DetectFields(IReadOnlyList<LogRecord> records)
    {
        return records.SelectMany(record => record.Fields)
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DetectedFieldSummary(
                group.Key,
                group.Count(),
                group.Select(pair => pair.Value!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToArray()))
            .OrderByDescending(summary => summary.Count)
            .ThenBy(summary => summary.Name)
            .Take(80)
            .ToArray();
    }

    public CorrelationTrace TraceByCorrelationId(IReadOnlyList<LogRecord> records, string correlationId)
    {
        var trace = records
            .Where(record => string.Equals(record.CorrelationId, correlationId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.TraceId, correlationId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.RequestId, correlationId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(record => record.Timestamp)
            .ToArray();

        return new CorrelationTrace(correlationId, trace.Length, trace);
    }

    public TenantClientFlowAnalysis AnalyzeTenantClientFlows(IReadOnlyList<LogRecord> records) =>
        tenantFlowAnalysisService.Analyze(records);

    private static bool IsErrorLike(LogRecord record)
    {
        var text = $"{record.Level} {record.Severity} {record.Message} {record.Log} {record.Exception} {record.StackTrace} {record.HttpStatusCode}";
        return record.HttpStatusCode >= 500
            || record.LevelKey.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
            || ContainsAny(text, "exception", "failed", "timeout", "timed out", "crashloopbackoff", "oomkilled", "unauthorized", "forbidden", "refused", "unavailable", "probe failed");
    }

    private static IReadOnlyList<ErrorGroup> Group(IEnumerable<LogRecord> records, Func<LogRecord, string> keySelector)
    {
        return records
            .GroupBy(keySelector, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ErrorGroup(
                group.Key,
                group.Count(),
                group.Min(record => record.Timestamp)?.ToString("O"),
                group.Max(record => record.Timestamp)?.ToString("O"),
                group.Take(3).Select(ToEvidence).ToArray()))
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.Key)
            .Take(20)
            .ToArray();
    }

    private static IReadOnlyList<string> DetectPatterns(IReadOnlyList<LogRecord> records, IReadOnlyList<LogRecord> errorRecords)
    {
        var patterns = new List<string>();
        var allText = string.Join('\n', records.Select(record => $"{record.Message} {record.Log} {record.Exception} {record.StackTrace} {record.HttpStatusCode}"));
        var errorText = string.Join('\n', errorRecords.Select(record => $"{record.Message} {record.Log} {record.Exception} {record.StackTrace} {record.HttpStatusCode}"));

        AddIf(patterns, errorRecords.Count >= 3, "Repeated errors");
        AddIf(patterns, ContainsAny(errorText, "timeout", "timed out", "TaskCanceledException", "SocketTimeoutException"), "Timeout errors");
        AddIf(patterns, ContainsAny(errorText, "retry", "retrying", "attempt 2", "attempt 3", "transient failure") && errorRecords.Count >= 3, "Retry storm");
        AddIf(patterns, errorRecords.Any(record => record.HttpStatusCode is >= 500 and <= 504), "HTTP 500/502/503/504 failures");
        AddIf(patterns, ContainsAny(errorText, "sql", "database", "db timeout", "deadlock", "connection pool", "DbException"), "Database failures");
        AddIf(patterns, ContainsAny(errorText, "queue", "kafka", "rabbit", "service bus", "topic", "consumer lag"), "Queue failures");
        AddIf(patterns, ContainsAny(errorText, "downstream", "dependency", "accountservice", "external api", "upstream"), "Downstream dependency failures");
        AddIf(patterns, ContainsAny(errorText, "unauthorized", "forbidden", "401", "403", "invalid token", "expired token"), "Authentication/authorization failures");
        AddIf(patterns, ContainsAny(allText, "oomkilled", "out of memory"), "OOMKilled");
        AddIf(patterns, ContainsAny(allText, "crashloopbackoff", "back-off restarting failed container"), "CrashLoopBackOff");
        AddIf(patterns, ContainsAny(allText, "readiness probe failed"), "Readiness probe failure");
        AddIf(patterns, ContainsAny(allText, "liveness probe failed"), "Liveness probe failure");
        AddIf(patterns, ContainsAny(allText, "dns", "nxdomain", "name resolution", "service discovery"), "DNS/service discovery failure");
        AddIf(patterns, ContainsAny(allText, "configmap", "secret not found", "missing secret", "configuration missing"), "ConfigMap/Secret issue");
        AddIf(patterns, ContainsAny(allText, "cpu pressure", "memory pressure", "evicted", "throttling"), "CPU/memory pressure indicators");

        return patterns.Count == 0 ? ["No strong known failure pattern detected"] : patterns;
    }

    private static RootCauseClassification Classify(IReadOnlyList<string> patterns, IReadOnlyList<LogRecord> errorRecords)
    {
        var text = string.Join(' ', patterns) + " " + string.Join(' ', errorRecords.Select(record => $"{record.Message} {record.Exception} {record.StackTrace}"));
        if (ContainsAny(text, "OOMKilled", "CrashLoopBackOff", "probe failure", "CPU/memory pressure", "Kubernetes")) return RootCauseClassification.KubernetesInfrastructureIssue;
        if (ContainsAny(text, "Database failures", "sql", "deadlock", "connection pool")) return RootCauseClassification.DatabaseIssue;
        if (ContainsAny(text, "Authentication/authorization", "unauthorized", "forbidden", "invalid token")) return RootCauseClassification.AuthenticationAuthorizationIssue;
        if (ContainsAny(text, "Timeout errors", "DNS/service discovery", "timeout", "timed out")) return RootCauseClassification.NetworkTimeoutIssue;
        if (ContainsAny(text, "ConfigMap/Secret", "configuration missing", "missing secret")) return RootCauseClassification.ConfigurationIssue;
        if (ContainsAny(text, "Downstream dependency", "HTTP 500/502/503/504", "dependency")) return RootCauseClassification.ExternalDependencyIssue;
        if (ContainsAny(text, "NullReferenceException", "InvalidOperationException", "JsonException", "serialization")) return RootCauseClassification.ApplicationCodeIssue;
        return errorRecords.Count == 0 ? RootCauseClassification.Unknown : RootCauseClassification.ApplicationCodeIssue;
    }

    private static string CalculateConfidence(RootCauseClassification rootCause, IReadOnlyList<LogRecord> errorRecords, IReadOnlyList<string> patterns)
    {
        if (rootCause == RootCauseClassification.Unknown || errorRecords.Count == 0)
        {
            return "Low";
        }

        if (errorRecords.Count >= 5 && patterns.Count >= 2)
        {
            return "High";
        }

        return "Medium";
    }

    private static AgentResponse BuildDeterministicResponse(
        IReadOnlyList<LogRecord> records,
        IReadOnlyList<LogRecord> errorRecords,
        IReadOnlyList<string> patterns,
        RootCauseClassification rootCause,
        string confidence,
        IReadOnlyList<string> evidence,
        TenantClientFlowAnalysis tenantFlowAnalysis,
        string? question,
        string? action)
    {
        var services = errorRecords.Select(record => record.ServiceKey).Where(value => value != "unknown-service").Distinct().Take(8).ToArray();
        var pods = errorRecords.Select(record => record.PodKey).Where(value => value != "unknown-pod").Distinct().Take(8).ToArray();
        var namespaces = errorRecords.Select(record => record.NamespaceKey).Where(value => value != "unknown-namespace").Distinct().Take(5).ToArray();
        var apis = errorRecords.Select(record => record.ApiPathKey).Where(value => value != "unknown-path").Distinct().Take(8).ToArray();
        var components = services.Concat(pods).Concat(namespaces).Concat(apis).Distinct().ToArray();
        var executive = errorRecords.Count == 0
            ? "No obvious error pattern is visible in the uploaded logs. More targeted logs around the incident window may be needed."
            : $"The uploaded logs show {errorRecords.Count} failure-like events across {services.Length} service(s). The strongest signal is {string.Join(", ", patterns.Take(3))}.";
        var technical = $"Analyzed {records.Count} records. Root cause classification is {FormatRootCause(rootCause)} with {confidence} confidence. Tenant/client flow analysis found {tenantFlowAnalysis.DeviatingCorrelationCount} deviating correlation(s), including {tenantFlowAnalysis.InvalidDeviationCount} invalid/problematic candidate(s).";
        var flowEvidence = tenantFlowAnalysis.Deviations
            .Take(5)
            .Select(deviation => $"{deviation.Validity}: tenant={deviation.TenantKey}, client={deviation.ClientKey}, process={deviation.ProcessKey}, correlation={deviation.CorrelationId}, observed={deviation.ObservedFlow}, expected={deviation.ExpectedFlow}")
            .ToArray();

        var response = new AgentResponse
        {
            ExecutiveSummary = executive,
            TechnicalSummary = technical,
            ImpactedComponents = components.Length == 0 ? ["Unknown from provided logs"] : components,
            ErrorPattern = string.Join("; ", patterns.Concat(tenantFlowAnalysis.DecisionNotes)),
            ProbableRootCause = $"{FormatRootCause(rootCause)} - {confidence} confidence",
            EvidenceFromLogs = evidence.Concat(flowEvidence).Take(25).ToArray(),
            OpsActions =
            [
                "Check status and recent restarts for only the impacted pods.",
                "Review Kubernetes events for the affected namespace and incident window.",
                "Check CPU, memory, readiness, and liveness metrics for impacted pods.",
                "Validate downstream dependency health before restarting anything.",
                "Escalate to developers when code, configuration, or dependency evidence is visible."
            ],
            DeveloperActions =
            [
                "Inspect the API path and exception stack shown in the evidence.",
                "Review timeout, retry, circuit breaker, and HTTP client configuration.",
                "Check database or downstream service calls around the failing correlation IDs.",
                "Validate configuration, secrets, connection strings, and deployment changes.",
                "Add targeted telemetry for missing correlation, exception, and dependency data."
            ],
            ImmediateMitigation = ["Investigate the impacted service or pod first; restart only the affected pod if Kubernetes evidence supports it."],
            DeveloperFix = ["Fix the failing code path, configuration, dependency handling, or timeout policy identified by the log evidence."],
            LongTermPrevention = ["Add SLO alerts, dependency health dashboards, bounded retries, circuit breakers, and structured log fields for correlation."],
            FollowUpQueries =
            [
                "Trace by correlationId for the top failing request.",
                "Group errors by pod for the incident window.",
                "Show only timeout and dependency failures.",
                "Prepare an incident report for business stakeholders."
            ]
        };

        return response with { Markdown = ToMarkdown(response, question, action) };
    }

    private static string ToMarkdown(AgentResponse response, string? question, string? action)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Splunk Log Analysis Summary");
        if (!string.IsNullOrWhiteSpace(question) || !string.IsNullOrWhiteSpace(action))
        {
            builder.AppendLine();
            builder.AppendLine($"Question/action: {question ?? action}");
        }

        AppendSection(builder, "1. Executive Summary", response.ExecutiveSummary);
        AppendSection(builder, "2. Technical Summary", response.TechnicalSummary);
        AppendList(builder, "3. Impacted Components", response.ImpactedComponents);
        AppendSection(builder, "4. Error Pattern", response.ErrorPattern);
        AppendSection(builder, "5. Probable Root Cause", response.ProbableRootCause);
        AppendList(builder, "6. Evidence from Logs", response.EvidenceFromLogs);
        AppendList(builder, "7. Ops Action", response.OpsActions);
        AppendList(builder, "8. Developer Action", response.DeveloperActions);
        builder.AppendLine("## 9. Suggested Fix");
        AppendList(builder, "Immediate Mitigation", response.ImmediateMitigation);
        AppendList(builder, "Developer Fix", response.DeveloperFix);
        AppendList(builder, "Long-term Prevention", response.LongTermPrevention);
        AppendList(builder, "10. Follow-up Queries", response.FollowUpQueries);
        return builder.ToString();
    }

    private static void AppendSection(StringBuilder builder, string title, string content)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        builder.AppendLine(content);
    }

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> items)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        foreach (var item in items.DefaultIfEmpty("No evidence available in uploaded logs."))
        {
            builder.AppendLine($"- {item}");
        }
    }

    private static string ToEvidence(LogRecord record)
    {
        var when = record.Timestamp?.ToString("O") ?? record.RawTime ?? "unknown-time";
        var text = FirstNonEmpty(record.Message, record.Log, record.Exception, record.MaskedRaw);
        return $"{when} | {record.ServiceKey} | {record.PodKey} | {record.LevelKey} | {text[..Math.Min(text.Length, 420)]}";
    }

    private static void AddIf(List<string> patterns, bool condition, string pattern)
    {
        if (condition)
        {
            patterns.Add(pattern);
        }
    }

    private static bool ContainsAny(string? value, params string[] tokens) =>
        !string.IsNullOrWhiteSpace(value) && tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static string FormatRootCause(RootCauseClassification classification) => classification switch
    {
        RootCauseClassification.ApplicationCodeIssue => "Application Code Issue",
        RootCauseClassification.ConfigurationIssue => "Configuration Issue",
        RootCauseClassification.DatabaseIssue => "Database Issue",
        RootCauseClassification.ExternalDependencyIssue => "External Dependency Issue",
        RootCauseClassification.KubernetesInfrastructureIssue => "Kubernetes/Infrastructure Issue",
        RootCauseClassification.AuthenticationAuthorizationIssue => "Authentication/Authorization Issue",
        RootCauseClassification.NetworkTimeoutIssue => "Network/Timeout Issue",
        RootCauseClassification.DeploymentVersionCompatibilityIssue => "Deployment/Version Compatibility Issue",
        RootCauseClassification.DataIssue => "Data Issue",
        _ => "Unknown / Needs More Logs"
    };
}
