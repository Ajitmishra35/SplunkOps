using System.Text;
using SplunkOpsRca.Infrastructure.Parsing;
using SplunkOpsRca.Infrastructure.Security;

namespace SplunkOpsRca.Tests;

public sealed class ParsingTests
{
    [Fact]
    public async Task Parses_Json_Array_And_Normalizes_Fields()
    {
        var parser = new SplunkJsonLogParser(new SensitiveDataMaskingService());
        await using var stream = ToStream("""
[
  {
    "_time": "2026-04-25T08:00:00Z",
    "severity": "ERROR",
    "serviceName": "PaymentService",
    "kubernetes": { "pod_name": "payment-7d9" },
    "path": "/api/payments",
    "httpStatusCode": 500,
    "message": "System.InvalidOperationException: payment failed"
  }
]
""");

        var records = await parser.ParseAsync(stream, CancellationToken.None);

        Assert.Single(records);
        Assert.Equal("PaymentService", records[0].ServiceKey);
        Assert.Equal("payment-7d9", records[0].PodKey);
        Assert.Equal("ERROR", records[0].Level);
        Assert.Equal("InvalidOperationException", records[0].ExceptionType);
    }

    [Fact]
    public async Task Parses_Ndjson_And_Expands_Nested_Message_Json()
    {
        var parser = new SplunkJsonLogParser(new SensitiveDataMaskingService());
        await using var stream = ToStream("""
{"_time":"2026-04-25T08:00:01Z","message":"{\"service\":\"AccountService\",\"level\":\"ERROR\",\"correlationId\":\"corr-1\",\"path\":\"/accounts/42\",\"durationMs\":31000,\"exception\":\"TimeoutException: timed out\"}"}
{"_time":"2026-04-25T08:00:02Z","service":"PaymentService","level":"INFO","message":"retrying corr-1"}
""");

        var records = await parser.ParseAsync(stream, CancellationToken.None);

        Assert.Equal(2, records.Count);
        Assert.Equal("AccountService", records[0].ServiceKey);
        Assert.Equal("corr-1", records[0].CorrelationId);
        Assert.Equal(31000, records[0].DurationMs);
    }

    [Fact]
    public async Task Parses_Tenant_Client_Thread_And_Process_Fields()
    {
        var parser = new SplunkJsonLogParser(new SensitiveDataMaskingService());
        await using var stream = ToStream("""
{"_time":"2026-04-25T08:00:01Z","tenantId":"tenant-a","clientId":"client-42","threadId":"worker-7","processName":"PaymentWorkflow","correlationId":"corr-flow-1","service":"PaymentService","level":"INFO","message":"payment started"}
""");

        var records = await parser.ParseAsync(stream, CancellationToken.None);

        Assert.Equal("tenant-a", records[0].TenantKey);
        Assert.Equal("client-42", records[0].ClientKey);
        Assert.Equal("worker-7", records[0].ThreadKey);
        Assert.Equal("PaymentWorkflow", records[0].ProcessKey);
    }

    private static MemoryStream ToStream(string value) => new(Encoding.UTF8.GetBytes(value));
}
