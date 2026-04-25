using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SplunkOpsRca.Application.Abstractions;
using SplunkOpsRca.Application.UseCases;
using SplunkOpsRca.Infrastructure.Agents;
using SplunkOpsRca.Infrastructure.AzureOpenAI;
using SplunkOpsRca.Infrastructure.Parsing;
using SplunkOpsRca.Infrastructure.Security;
using SplunkOpsRca.Infrastructure.Services;
using SplunkOpsRca.Infrastructure.Storage;

namespace SplunkOpsRca.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSplunkOpsRca(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AzureOpenAiOptions>(configuration.GetSection("AzureOpenAI"));
        services.AddSingleton<ISensitiveDataMaskingService, SensitiveDataMaskingService>();
        services.AddSingleton<ILogParser, SplunkJsonLogParser>();
        services.AddSingleton<ITenantFlowAnalysisService, TenantFlowAnalysisService>();
        services.AddSingleton<ILogAnalysisService, LogAnalysisService>();
        services.AddSingleton<ILogSessionStore, InMemoryLogSessionStore>();
        services.AddScoped<LogWorkflowService>();
        services.AddHttpClient<IAzureOpenAiChatService, AzureOpenAiChatService>();
        services.AddScoped<ISplunkOpsRcaAgent, SplunkOpsRcaAgent>();
        return services;
    }
}
