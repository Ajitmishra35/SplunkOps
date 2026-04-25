namespace SplunkOpsRca.Application.Abstractions;

public interface IAzureOpenAiChatService
{
    Task<string?> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken);
}
