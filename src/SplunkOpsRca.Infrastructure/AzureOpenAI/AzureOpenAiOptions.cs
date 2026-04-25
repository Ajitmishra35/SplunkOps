namespace SplunkOpsRca.Infrastructure.AzureOpenAI;

public sealed class AzureOpenAiOptions
{
    public string Endpoint { get; init; } = "";
    public string DeploymentName { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public string ApiVersion { get; init; } = "2024-10-21";
}
