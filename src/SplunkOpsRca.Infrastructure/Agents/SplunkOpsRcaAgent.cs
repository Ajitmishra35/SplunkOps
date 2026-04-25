using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using System.ClientModel;
using System.Text.Json;
using SplunkOpsRca.Application.Abstractions;
using SplunkOpsRca.Infrastructure.AzureOpenAI;
using SplunkOpsRca.Domain.Models;
using DomainAgentResponse = SplunkOpsRca.Domain.Models.AgentResponse;

namespace SplunkOpsRca.Infrastructure.Agents;

public sealed class SplunkOpsRcaAgent(
    IAzureOpenAiChatService fallbackChatService,
    ILogAnalysisService analysisService,
    IOptions<AzureOpenAiOptions> options,
    ILoggerFactory loggerFactory,
    IServiceProvider serviceProvider) : ISplunkOpsRcaAgent
{
    public const string AgentName = "SplunkOpsRcaAgent";

    public const string SystemPrompt = """
You are SplunkOps RCA Agent, an enterprise-grade AI operations assistant.

You analyze Splunk-exported JSON log files from Kubernetes-based microservice applications.

You act like a senior backend developer, SRE, and production support engineer.

Your job is to help Ops, Support, Developers, and Incident teams understand what is happening, which service or pod is affected, what the probable root cause is, what evidence exists in the logs, what Ops can safely do now, what developers should fix, and what should be monitored after the fix.

You also analyze multi-tenant and multi-client execution flows. Learn the normal successful flow from uploaded logs when evidence exists, identify tenants or clients whose process execution deviates, and classify deviations as expected/valid, invalid/problematic, or needs review. Never claim a baseline was learned when successful comparison logs are missing.

You must reason only from uploaded logs and provided context. Do not invent missing information. If evidence is incomplete, say so clearly.

Always classify root cause into one of these categories:
Application Code Issue, Configuration Issue, Database Issue, External Dependency Issue, Kubernetes/Infrastructure Issue, Authentication/Authorization Issue, Network/Timeout Issue, Deployment/Version Compatibility Issue, Data Issue, Unknown / Needs More Logs.

Always include confidence level:
High, Medium, or Low.

Always mask secrets and sensitive data.

When Ops asks a question, answer on behalf of a developer/SRE in clear language.

Do not recommend risky production actions unless evidence supports them.

Prefer investigation first, then mitigation, then permanent fix.
""";

    public async Task<DomainAgentResponse> AskAsync(LogAnalysisRequest request, LogAnalysisResult deterministicAnalysis, IReadOnlyList<LogRecord> records, CancellationToken cancellationToken)
    {
        var prompt = BuildPrompt(request, deterministicAnalysis, records);
        var completion = await CompleteWithMicrosoftAgentFrameworkAsync(request, deterministicAnalysis, records, prompt, cancellationToken)
            ?? await fallbackChatService.CompleteAsync(SystemPrompt, prompt, cancellationToken);

        if (string.IsNullOrWhiteSpace(completion))
        {
            return deterministicAnalysis.AgentResponse with
            {
                Markdown = """
Agent mode: deterministic fallback. Azure OpenAI is not configured, so Microsoft Agent Framework did not call the LLM/tools.

""" + deterministicAnalysis.AgentResponse.Markdown
            };
        }

        return deterministicAnalysis.AgentResponse with { Markdown = completion };
    }

    private async Task<string?> CompleteWithMicrosoftAgentFrameworkAsync(
        LogAnalysisRequest request,
        LogAnalysisResult deterministicAnalysis,
        IReadOnlyList<LogRecord> records,
        string prompt,
        CancellationToken cancellationToken)
    {
        var config = options.Value;
        if (!IsConfigured(config))
        {
            return null;
        }

        var azureClient = new AzureOpenAIClient(new Uri(config.Endpoint), new ApiKeyCredential(config.ApiKey));
        var chatClient = azureClient.GetChatClient(config.DeploymentName);
        var tools = CreateTools(request, deterministicAnalysis, records);

        var agent = chatClient.AsAIAgent(
            instructions: SystemPrompt,
            name: AgentName,
            description: "Analyzes Splunk-exported Kubernetes microservice logs and produces RCA guidance.",
            tools: tools,
            loggerFactory: loggerFactory,
            services: serviceProvider);

        var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken);
        return string.IsNullOrWhiteSpace(response.Text) ? response.ToString() : response.Text;
    }

    private IList<AITool> CreateTools(LogAnalysisRequest request, LogAnalysisResult deterministicAnalysis, IReadOnlyList<LogRecord> records)
    {
        return
        [
            AIFunctionFactory.Create(
                () => ToolResult(new
                {
                    request.SessionId,
                    request.Question,
                    request.Action,
                    deterministicAnalysis.RecordCount,
                    deterministicAnalysis.RootCause,
                    deterministicAnalysis.Confidence,
                    deterministicAnalysis.DetectedPatterns,
                    deterministicAnalysis.DetectedFields,
                    deterministicAnalysis.TenantClientFlowAnalysis.DecisionNotes
                }),
                name: "get_log_summary",
                description: "Returns the normalized Splunk log session summary, detected fields, patterns, root cause category, and confidence."),

            AIFunctionFactory.Create(
                () => ToolResult(new
                {
                    deterministicAnalysis.ErrorsByService,
                    deterministicAnalysis.ErrorsByPod,
                    deterministicAnalysis.ErrorsByException,
                    deterministicAnalysis.ErrorsByCorrelationId,
                    deterministicAnalysis.ErrorsByApiPath,
                    deterministicAnalysis.ErrorsByHttpStatusCode
                }),
                name: "get_grouped_error_statistics",
                description: "Returns grouped error statistics by service, pod, exception, correlation ID, API path, and HTTP status code."),

            AIFunctionFactory.Create(
                () => ToolResult(deterministicAnalysis.TenantClientFlowAnalysis),
                name: "analyze_tenant_client_flows",
                description: "Returns learned normal tenant/client execution flows, deviations, validity classification, evidence, and recommended actions."),

            AIFunctionFactory.Create(
                (string correlationId) => ToolResult(analysisService.TraceByCorrelationId(records, correlationId)),
                name: "trace_correlation_id",
                description: "Returns a timestamp-ordered trace for a correlationId, traceId, or requestId found in the uploaded logs."),

            AIFunctionFactory.Create(
                () => ToolResult(new
                {
                    deterministicAnalysis.AgentResponse.ExecutiveSummary,
                    deterministicAnalysis.AgentResponse.TechnicalSummary,
                    deterministicAnalysis.AgentResponse.ImpactedComponents,
                    deterministicAnalysis.AgentResponse.ProbableRootCause,
                    deterministicAnalysis.AgentResponse.OpsActions,
                    deterministicAnalysis.AgentResponse.DeveloperActions,
                    deterministicAnalysis.AgentResponse.ImmediateMitigation,
                    deterministicAnalysis.AgentResponse.DeveloperFix,
                    deterministicAnalysis.AgentResponse.LongTermPrevention
                }),
                name: "generate_incident_report_inputs",
                description: "Returns incident report inputs: impact, evidence, probable root cause, Ops actions, developer actions, mitigation, and prevention.")
        ];
    }

    private static string ToolResult(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });

    private static bool IsConfigured(AzureOpenAiOptions config) =>
        !string.IsNullOrWhiteSpace(config.Endpoint)
        && Uri.TryCreate(config.Endpoint, UriKind.Absolute, out _)
        && !string.IsNullOrWhiteSpace(config.DeploymentName)
        && !string.IsNullOrWhiteSpace(config.ApiKey)
        && !config.ApiKey.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
        && !config.Endpoint.Contains("your-azure-openai-resource", StringComparison.OrdinalIgnoreCase);

    private static string BuildPrompt(LogAnalysisRequest request, LogAnalysisResult analysis, IReadOnlyList<LogRecord> records)
    {
        var evidenceRecords = records
            .Where(record => analysis.Evidence.Any(evidence => evidence.Contains(record.ServiceKey, StringComparison.OrdinalIgnoreCase)))
            .Take(30)
            .Select(record => new
            {
                record.Timestamp,
                record.Level,
                Service = record.ServiceKey,
                Pod = record.PodKey,
                Namespace = record.NamespaceKey,
                record.CorrelationId,
                record.Path,
                record.HttpStatusCode,
                Message = record.Message,
                record.Exception
            });

        return $$"""
Agent: {{AgentName}}
User question/action: {{request.Question ?? request.Action ?? "Analyze the uploaded logs."}}

Return exactly this structure:
# Splunk Log Analysis Summary
## 1. Executive Summary
## 2. Technical Summary
## 3. Impacted Components
## 4. Error Pattern
## 5. Probable Root Cause
## 6. Evidence from Logs
## 7. Ops Action
## 8. Developer Action
## 9. Suggested Fix
### Immediate Mitigation
### Developer Fix
### Long-term Prevention
## 10. Follow-up Queries

Deterministic analysis:
{{JsonSerializer.Serialize(analysis, new JsonSerializerOptions { WriteIndented = true })}}

Masked evidence records:
{{JsonSerializer.Serialize(evidenceRecords, new JsonSerializerOptions { WriteIndented = true })}}

Use Microsoft Agent Framework orchestration conceptually as the tool coordinator. Available tool outputs are:
- get_log_summary
- get_grouped_error_statistics
- analyze_tenant_client_flows
- trace_correlation_id
- generate_incident_report_inputs

You should call tools before producing the final answer. In the final answer, include:
Agent mode: Microsoft Agent Framework + Azure OpenAI + tools

Important: do not add facts that are not present in the deterministic analysis or masked evidence.
""";
    }
}
