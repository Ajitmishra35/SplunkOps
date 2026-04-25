using SplunkOpsRca.Domain.Models;
using SplunkOpsRca.Infrastructure.Services;

namespace SplunkOpsRca.Tests;

public sealed class AnalysisTests
{
    [Fact]
    public void Groups_By_Service_Pod_Exception_And_CorrelationId()
    {
        var service = new LogAnalysisService();
        var records = BuildTimeoutRecords();

        var result = service.Analyze("session-1", records);

        Assert.Equal("PaymentService", result.ErrorsByService[0].Key);
        Assert.Equal("payment-abc", result.ErrorsByPod[0].Key);
        Assert.Contains(result.ErrorsByException, group => group.Key.Contains("TimeoutException"));
        Assert.Equal("corr-timeout-1", result.ErrorsByCorrelationId[0].Key);
    }

    [Fact]
    public void Traces_By_CorrelationId()
    {
        var service = new LogAnalysisService();

        var trace = service.TraceByCorrelationId(BuildTimeoutRecords(), "corr-timeout-1");

        Assert.Equal(3, trace.RecordCount);
        Assert.All(trace.Records, record => Assert.Equal("corr-timeout-1", record.CorrelationId));
    }

    [Fact]
    public void Classifies_Timeout_As_NetworkTimeoutIssue()
    {
        var service = new LogAnalysisService();

        var result = service.Analyze("session-1", BuildTimeoutRecords());

        Assert.Equal(RootCauseClassification.NetworkTimeoutIssue, result.RootCause);
        Assert.Contains("Timeout errors", result.DetectedPatterns);
    }

    [Fact]
    public void Classifies_OomKilled_As_KubernetesInfrastructureIssue()
    {
        var service = new LogAnalysisService();
        var records = new[]
        {
            new LogRecord { Level = "ERROR", Service = "CheckoutService", Pod = "checkout-1", Message = "Pod terminated with reason OOMKilled memory pressure", Fields = new Dictionary<string, string?> { ["message"] = "OOMKilled" } }
        };

        var result = service.Analyze("session-2", records);

        Assert.Equal(RootCauseClassification.KubernetesInfrastructureIssue, result.RootCause);
    }

    private static IReadOnlyList<LogRecord> BuildTimeoutRecords() =>
    [
        new LogRecord
        {
            Timestamp = DateTimeOffset.Parse("2026-04-25T08:00:00Z"),
            Level = "ERROR",
            Service = "PaymentService",
            Pod = "payment-abc",
            Namespace = "prod-payments",
            CorrelationId = "corr-timeout-1",
            Path = "/api/payments",
            HttpStatusCode = 504,
            Exception = "TimeoutException: timed out calling AccountService",
            ExceptionType = "TimeoutException",
            Message = "Timeout calling downstream AccountService",
            Fields = new Dictionary<string, string?> { ["service"] = "PaymentService", ["pod"] = "payment-abc" }
        },
        new LogRecord
        {
            Timestamp = DateTimeOffset.Parse("2026-04-25T08:00:02Z"),
            Level = "ERROR",
            Service = "PaymentService",
            Pod = "payment-abc",
            Namespace = "prod-payments",
            CorrelationId = "corr-timeout-1",
            Path = "/api/payments",
            HttpStatusCode = 504,
            Exception = "TimeoutException: timed out calling AccountService",
            ExceptionType = "TimeoutException",
            Message = "Retry attempt 2 failed with timeout",
            Fields = new Dictionary<string, string?> { ["service"] = "PaymentService", ["pod"] = "payment-abc" }
        },
        new LogRecord
        {
            Timestamp = DateTimeOffset.Parse("2026-04-25T08:00:04Z"),
            Level = "ERROR",
            Service = "PaymentService",
            Pod = "payment-abc",
            Namespace = "prod-payments",
            CorrelationId = "corr-timeout-1",
            Path = "/api/payments",
            HttpStatusCode = 504,
            Exception = "TimeoutException: timed out calling AccountService",
            ExceptionType = "TimeoutException",
            Message = "Retry attempt 3 failed with timeout",
            Fields = new Dictionary<string, string?> { ["service"] = "PaymentService", ["pod"] = "payment-abc" }
        }
    ];
}
