# Azure App Service Deployment

Build and publish either the API or Blazor Web app:

```powershell
dotnet publish src/SplunkOpsRca.Web/SplunkOpsRca.Web.csproj -c Release
dotnet publish src/SplunkOpsRca.Api/SplunkOpsRca.Api.csproj -c Release
```

Configure App Service application settings:

```text
AzureOpenAI__Endpoint=https://your-azure-openai-resource.openai.azure.com
AzureOpenAI__DeploymentName=your-chat-deployment
AzureOpenAI__ApiKey=<stored in App Service settings or Key Vault reference>
AzureOpenAI__ApiVersion=2024-10-21
```

For production, use managed identity and Key Vault references for secrets. Replace `InMemoryLogSessionStore` with Redis for short-lived sessions and Blob Storage, Cosmos DB, or SQL Server for durable upload history and audit records.
