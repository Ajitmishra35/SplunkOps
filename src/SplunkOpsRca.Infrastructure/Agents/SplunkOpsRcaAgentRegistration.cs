using SplunkOpsRca.Application.Abstractions;
using SplunkOpsRca.Domain.Models;

namespace SplunkOpsRca.Infrastructure.Agents;

public sealed record AgentToolDescriptor(string Name, string Description);

public static class SplunkOpsRcaAgentRegistration
{
    public static IReadOnlyList<AgentToolDescriptor> Tools { get; } =
    [
        new("log-summary", "Summarizes uploaded Splunk log sessions, detected fields, and failure-like records."),
        new("grouped-error-statistics", "Groups errors by service, pod, namespace, log level, exception, correlationId, API path, and HTTP status."),
        new("correlation-trace", "Builds a timestamp-ordered trace for a selected correlationId, traceId, or requestId."),
        new("incident-report", "Produces an incident summary with impact, probable root cause, immediate mitigation, developer fix, and prevention.")
    ];

    public static string AgentName => SplunkOpsRcaAgent.AgentName;
    public static string Instructions => SplunkOpsRcaAgent.SystemPrompt;

    // Microsoft Agent Framework integration point:
    // Keep the application layer on ISplunkOpsRcaAgent while package-specific APIs evolve.
    // A production implementation can register this prompt and these tool descriptors with
    // Microsoft.Agents.AI.OpenAI, then bind tool callbacks to ILogAnalysisService methods.
    public static object CreateFrameworkRegistrationPlaceholder(ILogAnalysisService analysisService) =>
        new
        {
            Name = AgentName,
            Instructions,
            Tools,
            Services = new
            {
                LogSummary = nameof(ILogAnalysisService.Analyze),
                GroupedErrorStatistics = nameof(LogAnalysisResult.ErrorsByService),
                CorrelationTrace = nameof(ILogAnalysisService.TraceByCorrelationId),
                IncidentReport = nameof(AgentResponse)
            },
            AnalysisService = analysisService.GetType().Name
        };
}
