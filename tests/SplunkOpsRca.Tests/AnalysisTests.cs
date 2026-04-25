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

    [Fact]
    public void Learns_Normal_Tenant_Client_Flow_And_Flags_Invalid_Deviation()
    {
        var service = new LogAnalysisService();
        var records = BuildTenantFlowRecords();

        var result = service.Analyze("session-flow", records);

        Assert.NotEmpty(result.TenantClientFlowAnalysis.LearnedNormalFlows);
        var deviation = Assert.Single(result.TenantClientFlowAnalysis.Deviations);
        Assert.Equal("tenant-a", deviation.TenantKey);
        Assert.Equal("client-bad", deviation.ClientKey);
        Assert.Equal("Invalid / Problematic", deviation.Validity);
        Assert.Contains("AccountService", deviation.ObservedFlow);
    }

    [Fact]
    public void Classifies_Expected_Tenant_Client_Deviation_When_Evidence_Says_Expected()
    {
        var service = new LogAnalysisService();
        var records = BuildTenantFlowRecords(includeExpectedDeviation: true);

        var result = service.Analyze("session-flow", records);

        Assert.Contains(result.TenantClientFlowAnalysis.Deviations, deviation =>
            deviation.ClientKey == "client-expected" && deviation.Validity == "Expected / Valid");
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

    private static IReadOnlyList<LogRecord> BuildTenantFlowRecords(bool includeExpectedDeviation = false)
    {
        var records = new List<LogRecord>
        {
            Flow("corr-good-1", "client-good", "PaymentWorkflow", "PaymentService", "INFO", "started", 0),
            Flow("corr-good-1", "client-good", "PaymentWorkflow", "AccountService", "INFO", "reserved account", 1),
            Flow("corr-good-1", "client-good", "PaymentWorkflow", "LedgerService", "INFO", "posted ledger", 2),
            Flow("corr-good-2", "client-good", "PaymentWorkflow", "PaymentService", "INFO", "started", 10),
            Flow("corr-good-2", "client-good", "PaymentWorkflow", "AccountService", "INFO", "reserved account", 11),
            Flow("corr-good-2", "client-good", "PaymentWorkflow", "LedgerService", "INFO", "posted ledger", 12),
            Flow("corr-bad-1", "client-bad", "PaymentWorkflow", "PaymentService", "INFO", "started", 20),
            Flow("corr-bad-1", "client-bad", "PaymentWorkflow", "AccountService", "ERROR", "TimeoutException failed calling database", 21, 504)
        };

        if (includeExpectedDeviation)
        {
            records.Add(Flow("corr-expected-1", "client-expected", "PaymentWorkflow", "PaymentService", "INFO", "expected business rule skipped ledger for test tenant", 30));
        }

        return records;
    }

    private static LogRecord Flow(string correlationId, string clientId, string processName, string service, string level, string message, int seconds, int? status = null) =>
        new()
        {
            Timestamp = DateTimeOffset.Parse("2026-04-25T08:00:00Z").AddSeconds(seconds),
            TenantId = "tenant-a",
            ClientId = clientId,
            ProcessName = processName,
            CorrelationId = correlationId,
            Service = service,
            Pod = $"{service.ToLowerInvariant()}-1",
            Level = level,
            Message = message,
            HttpStatusCode = status,
            Fields = new Dictionary<string, string?>
            {
                ["tenantId"] = "tenant-a",
                ["clientId"] = clientId,
                ["processName"] = processName,
                ["correlationId"] = correlationId,
                ["service"] = service
            }
        };
}
