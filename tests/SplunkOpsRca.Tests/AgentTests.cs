using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SplunkOpsRca.Application.Abstractions;
using SplunkOpsRca.Domain.Models;
using SplunkOpsRca.Infrastructure.Agents;
using SplunkOpsRca.Infrastructure.AzureOpenAI;
using SplunkOpsRca.Infrastructure.Services;

namespace SplunkOpsRca.Tests;

public sealed class AgentTests
{
    [Fact]
    public async Task Agent_Response_Is_Explicit_When_AzureOpenAI_Is_Not_Configured()
    {
        var analysisService = new LogAnalysisService();
        var records = new[]
        {
            new LogRecord { Level = "ERROR", Service = "PaymentService", Pod = "payment-1", Message = "TimeoutException: timed out", ExceptionType = "TimeoutException" }
        };
        var deterministic = analysisService.Analyze("session-1", records);
        var agent = new SplunkOpsRcaAgent(
            new NullChatService(),
            analysisService,
            Options.Create(new AzureOpenAiOptions { ApiKey = "placeholder-use-environment-variable" }),
            NullLoggerFactory.Instance,
            serviceProvider: null!);

        var response = await agent.AskAsync(new LogAnalysisRequest("session-1", "Find root cause", null), deterministic, records, CancellationToken.None);

        Assert.Contains("deterministic fallback", response.Markdown);
    }

    private sealed class NullChatService : IAzureOpenAiChatService
    {
        public Task<string?> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
