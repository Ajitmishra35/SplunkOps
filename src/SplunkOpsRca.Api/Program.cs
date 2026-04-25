using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using SplunkOpsRca.Application.UseCases;
using SplunkOpsRca.Domain.Models;
using SplunkOpsRca.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddSplunkOpsRca(builder.Configuration);
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyHeader()
            .AllowAnyMethod()
            .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["https://localhost:5001"]));
});

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddJsonConsole();

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GlobalExceptionHandler");
        logger.LogError(exception, "Unhandled API exception. TraceId: {TraceId}", traceId);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await Results.Problem(
            title: "Unexpected server error",
            detail: "The request failed. Use the traceId when contacting support.",
            extensions: new Dictionary<string, object?> { ["traceId"] = traceId })
            .ExecuteAsync(context);
    });
});

app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers.TryGetValue("x-correlation-id", out var value)
        ? value.ToString()
        : Guid.NewGuid().ToString("N");
    context.Response.Headers["x-correlation-id"] = correlationId;
    using (app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
    {
        await next();
    }
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors();

var group = app.MapGroup("/api/logs").WithTags("Splunk logs");

group.MapPost("/upload", async (HttpRequest request, LogWorkflowService workflow, CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { errors = new[] { "Upload must use multipart/form-data." } });
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("file");
    if (file is null)
    {
        return Results.BadRequest(new { errors = new[] { "Form field 'file' is required." } });
    }

    await using var stream = file.OpenReadStream();
    var result = await workflow.UploadAsync(file.FileName, file.Length, stream, cancellationToken);
    return result.ValidationErrors.Count > 0 ? Results.BadRequest(result) : Results.Ok(result);
})
.DisableAntiforgery()
.WithName("UploadSplunkLogs");

group.MapPost("/analyze", async (LogAnalysisRequest request, LogWorkflowService workflow, CancellationToken cancellationToken) =>
{
    var result = await workflow.AnalyzeAsync(request, cancellationToken);
    return result is null ? Results.NotFound(new { message = "Session was not found." }) : Results.Ok(result);
})
.WithName("AnalyzeSplunkLogs");

group.MapPost("/ask", async (LogAnalysisRequest request, LogWorkflowService workflow, CancellationToken cancellationToken) =>
{
    var result = await workflow.AnalyzeAsync(request, cancellationToken);
    return result is null ? Results.NotFound(new { message = "Session was not found." }) : Results.Ok(result.AgentResponse);
})
.WithName("AskSplunkOpsRcaAgent");

group.MapGet("/{sessionId}/summary", async (string sessionId, LogWorkflowService workflow, CancellationToken cancellationToken) =>
{
    var session = await workflow.GetSummaryAsync(sessionId, cancellationToken);
    return session is null
        ? Results.NotFound(new { message = "Session was not found." })
        : Results.Ok(new { session.SessionId, session.FileName, session.UploadedAt, RecordCount = session.Records.Count, session.DetectedFields });
})
.WithName("GetLogSessionSummary");

group.MapGet("/{sessionId}/errors", async (string sessionId, LogWorkflowService workflow, CancellationToken cancellationToken) =>
{
    var errors = await workflow.GetErrorsAsync(sessionId, cancellationToken);
    return errors is null ? Results.NotFound(new { message = "Session was not found." }) : Results.Ok(errors);
})
.WithName("GetGroupedErrors");

group.MapGet("/{sessionId}/correlation/{correlationId}", async (string sessionId, string correlationId, LogWorkflowService workflow, CancellationToken cancellationToken) =>
{
    var trace = await workflow.GetCorrelationTraceAsync(sessionId, correlationId, cancellationToken);
    return trace is null ? Results.NotFound(new { message = "Session was not found." }) : Results.Ok(trace);
})
.WithName("TraceCorrelationId");

group.MapGet("/{sessionId}/tenant-flows", async (string sessionId, LogWorkflowService workflow, CancellationToken cancellationToken) =>
{
    var flows = await workflow.GetTenantClientFlowsAsync(sessionId, cancellationToken);
    return flows is null ? Results.NotFound(new { message = "Session was not found." }) : Results.Ok(flows);
})
.WithName("AnalyzeTenantClientFlows");

app.Run();
