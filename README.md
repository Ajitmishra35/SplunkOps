# SplunkOps RCA Agent

SplunkOps RCA Agent is a production-shaped .NET 10 starter application for analyzing Splunk-exported JSON logs from Kubernetes-based microservice systems. It gives Ops, Support, Developers, SREs, and incident teams a secure upload flow, deterministic log analysis, Azure OpenAI backed explanations, and a Microsoft Agent Framework ready orchestration boundary.

## Architecture

```text
src/
  SplunkOpsRca.Web/             Blazor Server UI
  SplunkOpsRca.Api/             Minimal API endpoints
  SplunkOpsRca.Application/     Use cases, DTOs, interfaces
  SplunkOpsRca.Domain/          Domain models
  SplunkOpsRca.Infrastructure/  Parsing, masking, storage, Azure OpenAI, agent adapter
tests/
  SplunkOpsRca.Tests/           Unit tests
samples/                        Splunk JSON and NDJSON examples
deploy/                         Kubernetes and Azure deployment notes
```

The UI and API both call `LogWorkflowService`, so parsing, masking, grouping, deterministic RCA, and agent response behavior remain consistent.

## Tech Stack

- .NET 10
- Blazor Server
- ASP.NET Core Minimal APIs
- Microsoft Agent Framework package reference: `Microsoft.Agents.AI.OpenAI`
- Azure OpenAI Chat Completions REST integration
- xUnit tests

The infrastructure layer uses `ISplunkOpsRcaAgent` and `IAzureOpenAiChatService` abstractions. The current adapter creates a Microsoft Agent Framework `ChatClientAgent` from Azure OpenAI and registers real `AIFunction` tools:

- `get_log_summary`
- `get_grouped_error_statistics`
- `analyze_tenant_client_flows`
- `trace_correlation_id`
- `generate_incident_report_inputs`

## Run Locally

```powershell
dotnet restore
dotnet test
dotnet run --project src/SplunkOpsRca.Web/SplunkOpsRca.Web.csproj
```

Run the API separately:

```powershell
dotnet run --project src/SplunkOpsRca.Api/SplunkOpsRca.Api.csproj
```

Upload one of the files from `samples/` in the Blazor UI.

## Configure Azure OpenAI

Use environment variables or user secrets. Do not store real keys in `appsettings.json`.

```powershell
$env:AzureOpenAI__Endpoint="https://your-azure-openai-resource.openai.azure.com"
$env:AzureOpenAI__DeploymentName="your-chat-deployment"
$env:AzureOpenAI__ApiKey="<your-key>"
$env:AzureOpenAI__ApiVersion="2024-10-21"
```

If Azure OpenAI is not configured, the app falls back to deterministic local analysis and the response starts with `Agent mode: deterministic fallback`. When Azure OpenAI is configured correctly, the final answer is produced by Microsoft Agent Framework + Azure OpenAI + tools.

## API Endpoints

- `POST /api/logs/upload`
- `POST /api/logs/analyze`
- `POST /api/logs/ask`
- `GET /api/logs/{sessionId}/summary`
- `GET /api/logs/{sessionId}/errors`
- `GET /api/logs/{sessionId}/correlation/{correlationId}`
- `GET /api/logs/{sessionId}/tenant-flows`

Uploads use multipart form data with field name `file`.

## Supported Splunk Log Shapes

The parser supports:

- JSON arrays
- Newline-delimited JSON
- Nested JSON objects
- JSON encoded inside `message`, `log`, or `_raw`
- Common Splunk, OpenTelemetry, Kubernetes, and application log field variants

Normalized fields include timestamp, `_time`, level, severity, service, pod, namespace, container, source, sourcetype, index, message, exception, stack trace, trace/correlation/request IDs, tenant/client/user identifiers, thread/process identifiers, HTTP method/path/status, and duration.

## Tenant / Client Flow Intelligence

The application now includes deterministic multi-tenant flow analysis:

- Groups records by tenant, client, process, and correlation ID
- Learns dominant successful execution signatures from clean correlations
- Compares tenant/client correlations against learned normal flow
- Detects sequence, duration, and failure deviations
- Classifies deviations as `Expected / Valid`, `Invalid / Problematic`, or `Needs Review`
- Adds evidence and recommended action for each deviation

This is intentionally evidence-based. If uploaded logs do not contain successful comparison flows or tenant/client identifiers, the response says what is missing instead of inventing a baseline.

## Example Ops Questions

- What is happening?
- Which service or pod is failing?
- Find root cause.
- Show top errors.
- Group errors by pod.
- Suggest Kubernetes checks.
- Prepare an incident report.
- Analyze tenant/client flow.
- Find tenant deviations.
- Compare normal vs abnormal execution.

## Example Developer Questions

- Which API path is failing?
- Is this a database timeout or downstream dependency issue?
- Trace by correlationId.
- What code path should developers inspect?
- Suggest code fixes.

## Security Masking

Before logs are displayed or sent to Azure OpenAI, `SensitiveDataMaskingService` masks:

- Passwords
- Tokens
- JWTs
- API keys
- Authorization headers
- Connection strings
- Account numbers
- PAN, Aadhaar, and card-like numbers
- Emails

Masking is intentionally conservative. Extend the service for organization-specific identifiers, customer fields, and regional compliance rules.

## Production Notes

The MVP uses `InMemoryLogSessionStore` for session uploads. Replace it for production:

- Redis for short-lived uploaded session state
- Azure Blob Storage for raw masked upload payloads
- Cosmos DB or SQL Server for durable metadata, audit records, and incident history
- Azure Key Vault for Azure OpenAI credentials
- OpenTelemetry for traces and metrics
- Centralized structured logs in Splunk, Azure Monitor, or your enterprise observability stack

Recommended hardening:

- Add authentication and authorization
- Enforce tenant isolation
- Add malware scanning for uploads
- Add private networking to Azure OpenAI
- Add prompt and response audit logging with sensitive-data controls
- Add persistent incident report export
- Add background processing for very large files

## Docker

Build API:

```powershell
docker build --target api -t splunkops-rca-api:local .
```

Build Web:

```powershell
docker build --target web -t splunkops-rca-web:local .
```

## Kubernetes

See `deploy/kubernetes.yaml`. Store secrets with External Secrets, Azure Key Vault CSI, or your platform standard. The included YAML is a deployable starting point, not a complete enterprise platform baseline.

## Future Enhancements

- Durable multi-tenant sessions and audit trails
- Role-based incident workspaces
- Splunk API integration in addition to file upload
- Kubernetes API read-only diagnostics
- Timeline visualization by correlation ID
- Exportable incident report and postmortem draft
- Domain-specific masking rules by tenant
- Agent Framework workflows with typed tool routing and checkpointing
