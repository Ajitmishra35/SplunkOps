using System.Globalization;
using System.Text;
using System.Text.Json;
using SplunkOpsRca.Application.Abstractions;
using SplunkOpsRca.Domain.Models;

namespace SplunkOpsRca.Infrastructure.Parsing;

public sealed class SplunkJsonLogParser(ISensitiveDataMaskingService maskingService) : ILogParser
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<LogRecord>> ParseAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var content = await reader.ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var rows = TryParseJsonArray(content, cancellationToken) ?? ParseNewlineDelimitedJson(content);
        return rows.Select(ToLogRecord).ToArray();
    }

    private static IReadOnlyList<JsonElement>? TryParseJsonArray(string content, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            cancellationToken.ThrowIfCancellationRequested();
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                return document.RootElement.EnumerateArray().Select(element => element.Clone()).ToArray();
            }

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return [document.RootElement.Clone()];
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static IReadOnlyList<JsonElement> ParseNewlineDelimitedJson(string content)
    {
        var rows = new List<JsonElement>();
        foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                rows.Add(document.RootElement.Clone());
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Invalid JSON log line: {line[..Math.Min(line.Length, 160)]}", ex);
            }
        }

        return rows;
    }

    private LogRecord ToLogRecord(JsonElement element)
    {
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Flatten(element, fields);
        ExpandNestedJson(fields, "message");
        ExpandNestedJson(fields, "log");
        ExpandNestedJson(fields, "_raw");

        var raw = maskingService.Mask(element.GetRawText());
        var exception = Pick(fields, "exception", "error.exception", "thrown", "error");
        var stackTrace = Pick(fields, "stackTrace", "stack_trace", "exception.stackTrace");
        var message = Pick(fields, "message", "msg", "_raw", "event.message");
        var log = Pick(fields, "log", "raw");

        return new LogRecord
        {
            RawTime = Pick(fields, "_time", "time", "timestamp", "@timestamp"),
            Timestamp = ParseTimestamp(Pick(fields, "_time", "time", "timestamp", "@timestamp")),
            Level = NormalizeLevel(Pick(fields, "level", "log.level", "severity", "severityText")),
            Severity = Pick(fields, "severity", "severityText"),
            Service = Pick(fields, "service", "service.name", "app", "application", "kubernetes.labels.app"),
            ServiceName = Pick(fields, "serviceName", "service_name", "service.name"),
            Pod = Pick(fields, "pod", "pod_name", "kubernetes.pod_name", "kubernetes.pod.name"),
            Namespace = Pick(fields, "namespace", "kubernetes.namespace_name", "kubernetes.namespace.name"),
            Container = Pick(fields, "container", "container_name", "kubernetes.container_name"),
            Host = Pick(fields, "host", "hostname", "kubernetes.host"),
            Source = Pick(fields, "source"),
            SourceType = Pick(fields, "sourcetype"),
            Index = Pick(fields, "index"),
            Message = maskingService.Mask(message ?? ""),
            Log = maskingService.Mask(log ?? ""),
            Exception = maskingService.Mask(exception ?? ""),
            StackTrace = maskingService.Mask(stackTrace ?? ""),
            TraceId = Pick(fields, "traceId", "trace_id", "trace.id"),
            SpanId = Pick(fields, "spanId", "span_id", "span.id"),
            CorrelationId = Pick(fields, "correlationId", "correlation_id", "x-correlation-id", "operation_Id"),
            RequestId = Pick(fields, "requestId", "request_id"),
            TenantId = Pick(fields, "tenantId", "tenant_id"),
            OrganizationId = Pick(fields, "organizationId", "organization_id"),
            UserId = Pick(fields, "userId", "user_id", "user"),
            Path = Pick(fields, "path", "request.path", "http.path", "url.path", "route"),
            Method = Pick(fields, "method", "http.method", "request.method"),
            HttpStatusCode = ParseInt(Pick(fields, "httpStatusCode", "status", "statusCode", "http.status_code", "response.status")),
            DurationMs = ParseDouble(Pick(fields, "durationMs", "elapsedMs", "duration", "latency_ms", "request.duration_ms")),
            ExceptionType = ExtractExceptionType(exception, stackTrace, message),
            MaskedRaw = raw,
            Fields = fields.ToDictionary(pair => pair.Key, pair => (string?)maskingService.Mask(pair.Value ?? ""), StringComparer.OrdinalIgnoreCase)
        };
    }

    private static void Flatten(JsonElement element, Dictionary<string, string?> target, string prefix = "")
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            var name = string.IsNullOrWhiteSpace(prefix) ? property.Name : $"{prefix}.{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                Flatten(property.Value, target, name);
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                target[name] = property.Value.GetRawText();
            }
            else
            {
                target[name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
            }
        }
    }

    private static void ExpandNestedJson(Dictionary<string, string?> fields, string key)
    {
        if (!fields.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}'))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var nested = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            Flatten(document.RootElement, nested);
            foreach (var pair in nested)
            {
                fields.TryAdd(pair.Key, pair.Value);
                fields.TryAdd($"{key}.{pair.Key}", pair.Value);
            }
        }
        catch (JsonException)
        {
        }
    }

    private static string? Pick(Dictionary<string, string?> fields, params string[] names)
    {
        foreach (var name in names)
        {
            if (fields.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var epoch))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(epoch > 10_000_000_000 ? (long)epoch : (long)(epoch * 1000));
        }

        return null;
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static string? NormalizeLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            return null;
        }

        var normalized = level.Trim().ToUpperInvariant();
        return normalized switch
        {
            "WARN" or "WARNING" => "WARN",
            "ERR" or "ERROR" or "FATAL" or "CRITICAL" => "ERROR",
            "INFO" or "INFORMATION" => "INFO",
            "DBG" or "DEBUG" => "DEBUG",
            _ => normalized
        };
    }

    private static string? ExtractExceptionType(params string?[] values)
    {
        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            var tokens = value!.Split([' ', ':', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var exception = tokens.FirstOrDefault(token => token.EndsWith("Exception", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exception))
            {
                return exception.Trim().Split('.').Last();
            }
        }

        return null;
    }
}
