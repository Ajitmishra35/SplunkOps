using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SplunkOpsRca.Application.Abstractions;

namespace SplunkOpsRca.Infrastructure.AzureOpenAI;

public sealed class AzureOpenAiChatService(HttpClient httpClient, IOptions<AzureOpenAiOptions> options) : IAzureOpenAiChatService
{
    public async Task<string?> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var config = options.Value;
        if (string.IsNullOrWhiteSpace(config.Endpoint)
            || string.IsNullOrWhiteSpace(config.DeploymentName)
            || string.IsNullOrWhiteSpace(config.ApiKey)
            || config.ApiKey.Contains("placeholder", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var endpoint = config.Endpoint.TrimEnd('/');
        var url = $"{endpoint}/openai/deployments/{Uri.EscapeDataString(config.DeploymentName)}/chat/completions?api-version={Uri.EscapeDataString(config.ApiVersion)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("api-key", config.ApiKey);
        request.Content = JsonContent.Create(new
        {
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature = 0.1,
            max_tokens = 2400
        });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
    }
}
