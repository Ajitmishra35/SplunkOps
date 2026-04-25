using SplunkOpsRca.Application.Abstractions;
using SplunkOpsRca.Domain.Models;

namespace SplunkOpsRca.Infrastructure.Services;

public sealed class TenantFlowAnalysisService : ITenantFlowAnalysisService
{
    public TenantClientFlowAnalysis Analyze(IReadOnlyList<LogRecord> records)
    {
        if (records.Count == 0)
        {
            return new TenantClientFlowAnalysis
            {
                DecisionNotes = ["No logs were available for tenant/client flow analysis."]
            };
        }

        var correlations = BuildCorrelationFlows(records);
        var baselines = LearnBaselines(correlations);
        var deviations = DetectDeviations(correlations, baselines);
        var summaries = BuildTenantClientSummaries(records, correlations, baselines, deviations);

        return new TenantClientFlowAnalysis
        {
            TotalTenants = records.Select(record => record.TenantKey).Where(key => key != "unknown-tenant").Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            TotalClients = records.Select(record => record.ClientKey).Where(key => key != "unknown-client").Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            TotalCorrelations = correlations.Count,
            DeviatingCorrelationCount = deviations.Select(deviation => deviation.CorrelationId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            InvalidDeviationCount = deviations.Count(deviation => deviation.Validity == "Invalid / Problematic"),
            ExpectedDeviationCount = deviations.Count(deviation => deviation.Validity == "Expected / Valid"),
            NeedsReviewDeviationCount = deviations.Count(deviation => deviation.Validity == "Needs Review"),
            LearnedNormalFlows = baselines.Values
                .OrderByDescending(flow => flow.SuccessfulCorrelationCount)
                .ThenBy(flow => flow.TenantKey)
                .Take(30)
                .ToArray(),
            TenantClientSummaries = summaries,
            Deviations = deviations,
            DecisionNotes = BuildDecisionNotes(records, correlations, baselines, deviations)
        };
    }

    private static IReadOnlyList<CorrelationFlow> BuildCorrelationFlows(IReadOnlyList<LogRecord> records)
    {
        return records
            .GroupBy(record => new
            {
                Tenant = record.TenantKey,
                Client = record.ClientKey,
                Process = record.ProcessKey,
                Correlation = record.CorrelationKey
            })
            .Where(group => group.Key.Correlation != "unknown-correlation" || group.Count() > 1)
            .Select(group =>
            {
                var ordered = group.OrderBy(record => record.Timestamp ?? DateTimeOffset.MinValue).ToArray();
                var signature = BuildSignature(ordered);
                return new CorrelationFlow(
                    group.Key.Tenant,
                    group.Key.Client,
                    group.Key.Process,
                    group.Key.Correlation,
                    signature,
                    ordered.Length,
                    ordered.Count(IsErrorLike),
                    CalculateDuration(ordered),
                    ordered);
            })
            .OrderBy(flow => flow.TenantKey)
            .ThenBy(flow => flow.ClientKey)
            .ThenBy(flow => flow.ProcessKey)
            .ThenBy(flow => flow.CorrelationId)
            .ToArray();
    }

    private static Dictionary<string, NormalFlowSignature> LearnBaselines(IReadOnlyList<CorrelationFlow> correlations)
    {
        return correlations
            .Where(flow => flow.ErrorCount == 0 && !ContainsAny(FlowText(flow), "failed", "timeout", "exception", "retry exhausted", "expected", "business rule", "skipped by rule", "test tenant"))
            .GroupBy(flow => BaselineKey(flow.TenantKey, flow.ClientKey, flow.ProcessKey))
            .Select(group =>
            {
                var dominant = group.GroupBy(flow => flow.Signature, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(signatureGroup => signatureGroup.Count())
                    .ThenBy(signatureGroup => signatureGroup.Key)
                    .First();
                var durations = dominant.Select(flow => flow.DurationMs).Where(duration => duration > 0).Order().ToArray();
                var median = durations.Length == 0 ? 0 : durations[durations.Length / 2];
                var sample = dominant.First();
                return new NormalFlowSignature(
                    sample.TenantKey,
                    sample.ClientKey,
                    sample.ProcessKey,
                    dominant.Key,
                    dominant.Count(),
                    median);
            })
            .ToDictionary(flow => BaselineKey(flow.TenantKey, flow.ClientKey, flow.ProcessKey), StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<FlowDeviation> DetectDeviations(
        IReadOnlyList<CorrelationFlow> correlations,
        IReadOnlyDictionary<string, NormalFlowSignature> baselines)
    {
        var deviations = new List<FlowDeviation>();
        foreach (var flow in correlations)
        {
            var baseline = FindBaseline(flow, baselines);
            var expectedFlow = baseline?.Signature ?? "No learned normal flow for this tenant/client/process";
            var differsFromBaseline = baseline is not null && !string.Equals(flow.Signature, baseline.Signature, StringComparison.OrdinalIgnoreCase);
            var slowComparedToBaseline = baseline is not null && baseline.MedianDurationMs > 0 && flow.DurationMs > baseline.MedianDurationMs * 2;
            var hasFailures = flow.ErrorCount > 0 || ContainsAny(FlowText(flow), "failed", "timeout", "exception", "unauthorized", "forbidden", "retry");

            if (!differsFromBaseline && !slowComparedToBaseline && !hasFailures)
            {
                continue;
            }

            var text = FlowText(flow);
            var validity = ClassifyValidity(text, hasFailures);
            var severity = validity == "Invalid / Problematic" && ContainsAny(text, "oomkilled", "crashloopbackoff", "out of memory", "500", "502", "503", "504", "timeout")
                ? "High"
                : validity == "Invalid / Problematic" ? "Medium" : "Low";
            var deviationType = BuildDeviationType(baseline, differsFromBaseline, slowComparedToBaseline, hasFailures);

            deviations.Add(new FlowDeviation(
                flow.TenantKey,
                flow.ClientKey,
                flow.ProcessKey,
                flow.CorrelationId,
                deviationType,
                expectedFlow,
                flow.Signature,
                validity,
                severity,
                baseline is null ? "Low" : hasFailures || differsFromBaseline ? "Medium" : "Low",
                flow.Records.Where(IsErrorLike).Take(5).Select(ToEvidence).DefaultIfEmpty(ToEvidence(flow.Records.Last())).ToArray(),
                BuildRecommendedAction(validity, deviationType, flow)));
        }

        return deviations
            .OrderByDescending(deviation => SeverityRank(deviation.Severity))
            .ThenBy(deviation => deviation.Validity)
            .ThenBy(deviation => deviation.TenantKey)
            .Take(50)
            .ToArray();
    }

    private static IReadOnlyList<TenantClientFlowSummary> BuildTenantClientSummaries(
        IReadOnlyList<LogRecord> records,
        IReadOnlyList<CorrelationFlow> correlations,
        IReadOnlyDictionary<string, NormalFlowSignature> baselines,
        IReadOnlyList<FlowDeviation> deviations)
    {
        return records
            .GroupBy(record => new { Tenant = record.TenantKey, Client = record.ClientKey })
            .Select(group =>
            {
                var tenantFlows = correlations
                    .Where(flow => string.Equals(flow.TenantKey, group.Key.Tenant, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(flow.ClientKey, group.Key.Client, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var tenantBaselines = baselines.Values
                    .Where(flow => string.Equals(flow.TenantKey, group.Key.Tenant, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(flow.ClientKey, group.Key.Client, StringComparison.OrdinalIgnoreCase))
                    .Select(flow => flow.Signature)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .ToArray();
                var tenantDeviations = deviations
                    .Where(deviation => string.Equals(deviation.TenantKey, group.Key.Tenant, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(deviation.ClientKey, group.Key.Client, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var invalidCount = tenantDeviations.Count(deviation => deviation.Validity == "Invalid / Problematic");
                var decision = invalidCount > 0
                    ? $"Problematic deviation detected in {invalidCount} correlation(s)."
                    : tenantDeviations.Length > 0 ? "Deviation present, but evidence suggests expected or needs review." : "Observed flow matches learned baseline or has no failure evidence.";

                return new TenantClientFlowSummary(
                    group.Key.Tenant,
                    group.Key.Client,
                    group.Count(),
                    tenantFlows.Length,
                    group.Count(IsErrorLike),
                    tenantDeviations.Length,
                    group.Select(record => record.ServiceKey).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray(),
                    group.Select(record => record.ProcessKey).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray(),
                    tenantBaselines,
                    tenantFlows.Select(flow => flow.Signature).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray(),
                    decision);
            })
            .OrderByDescending(summary => summary.DeviationCount)
            .ThenByDescending(summary => summary.ErrorCount)
            .ThenBy(summary => summary.TenantKey)
            .Take(40)
            .ToArray();
    }

    private static NormalFlowSignature? FindBaseline(CorrelationFlow flow, IReadOnlyDictionary<string, NormalFlowSignature> baselines)
    {
        if (baselines.TryGetValue(BaselineKey(flow.TenantKey, flow.ClientKey, flow.ProcessKey), out var exact))
        {
            return exact;
        }

        return baselines.Values
            .Where(candidate => string.Equals(candidate.TenantKey, flow.TenantKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.ProcessKey, flow.ProcessKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.SuccessfulCorrelationCount)
            .FirstOrDefault()
            ?? baselines.Values
                .Where(candidate => string.Equals(candidate.ProcessKey, flow.ProcessKey, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.SuccessfulCorrelationCount)
                .FirstOrDefault();
    }

    private static IReadOnlyList<string> BuildDecisionNotes(
        IReadOnlyList<LogRecord> records,
        IReadOnlyList<CorrelationFlow> correlations,
        IReadOnlyDictionary<string, NormalFlowSignature> baselines,
        IReadOnlyList<FlowDeviation> deviations)
    {
        var notes = new List<string>
        {
            $"Analyzed {records.Count} record(s) across {correlations.Count} correlation/process flow(s).",
            baselines.Count == 0
                ? "No clean successful flow baseline was learned; collect successful logs for stronger valid/invalid deviation decisions."
                : $"Learned {baselines.Count} dominant normal flow baseline(s) from successful correlations.",
            deviations.Count == 0
                ? "No tenant/client deviation was detected from available evidence."
                : $"Detected {deviations.Count} tenant/client deviation candidate(s), including {deviations.Count(deviation => deviation.Validity == "Invalid / Problematic")} problematic candidate(s)."
        };

        if (records.Any(record => record.TenantKey == "unknown-tenant" || record.ClientKey == "unknown-client"))
        {
            notes.Add("Some records are missing tenant/client identifiers; add tenantId/clientId/customerId to logs for reliable multi-tenant decisions.");
        }

        return notes;
    }

    private static string BuildSignature(IReadOnlyList<LogRecord> records)
    {
        var steps = records
            .Select(record => FirstNonEmpty(record.ServiceKey, record.ApiPathKey, record.ProcessKey))
            .Where(step => step is not "unknown-service" and not "unknown-path" and not "unknown-process")
            .Aggregate(new List<string>(), (steps, step) =>
            {
                if (steps.Count == 0 || !string.Equals(steps[^1], step, StringComparison.OrdinalIgnoreCase))
                {
                    steps.Add(step);
                }

                return steps;
            });

        return steps.Count == 0 ? "unknown-flow" : string.Join(" -> ", steps);
    }

    private static string BuildDeviationType(NormalFlowSignature? baseline, bool differsFromBaseline, bool slowComparedToBaseline, bool hasFailures)
    {
        var types = new List<string>();
        if (baseline is null)
        {
            types.Add("No learned baseline");
        }
        else if (differsFromBaseline)
        {
            types.Add("Sequence deviation");
        }

        if (slowComparedToBaseline)
        {
            types.Add("Duration deviation");
        }

        if (hasFailures)
        {
            types.Add("Failure deviation");
        }

        return string.Join(", ", types);
    }

    private static string ClassifyValidity(string text, bool hasFailures)
    {
        if (ContainsAny(text, "expected", "valid deviation", "business rule", "feature flag", "skipped by rule", "dry run", "test tenant"))
        {
            return "Expected / Valid";
        }

        if (hasFailures)
        {
            return "Invalid / Problematic";
        }

        return "Needs Review";
    }

    private static string BuildRecommendedAction(string validity, string deviationType, CorrelationFlow flow)
    {
        if (validity == "Expected / Valid")
        {
            return "Document the expected exception path and consider adding an explicit structured field such as deviationReason=expected.";
        }

        if (validity == "Invalid / Problematic")
        {
            return $"Investigate tenant {flow.TenantKey}, client {flow.ClientKey}, correlation {flow.CorrelationId}; compare observed flow with normal baseline and inspect failing service/pod evidence.";
        }

        return "Review with the application owner or business rules because the flow differs from baseline but does not contain clear failure evidence.";
    }

    private static bool IsErrorLike(LogRecord record)
    {
        var text = $"{record.Level} {record.Severity} {record.Message} {record.Log} {record.Exception} {record.StackTrace} {record.HttpStatusCode}";
        return record.HttpStatusCode >= 500
            || record.LevelKey.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
            || ContainsAny(text, "exception", "failed", "timeout", "timed out", "crashloopbackoff", "oomkilled", "unauthorized", "forbidden", "refused", "unavailable", "probe failed");
    }

    private static double CalculateDuration(IReadOnlyList<LogRecord> records)
    {
        var explicitDuration = records.Where(record => record.DurationMs.HasValue).Sum(record => record.DurationMs!.Value);
        if (explicitDuration > 0)
        {
            return explicitDuration;
        }

        var timestamps = records.Select(record => record.Timestamp).Where(timestamp => timestamp.HasValue).Select(timestamp => timestamp!.Value).Order().ToArray();
        return timestamps.Length < 2 ? 0 : (timestamps[^1] - timestamps[0]).TotalMilliseconds;
    }

    private static string FlowText(CorrelationFlow flow) =>
        string.Join(' ', flow.Records.Select(record => $"{record.Level} {record.HttpStatusCode} {record.Message} {record.Log} {record.Exception} {record.StackTrace}"));

    private static string ToEvidence(LogRecord record)
    {
        var when = record.Timestamp?.ToString("O") ?? record.RawTime ?? "unknown-time";
        var text = FirstNonEmpty(record.Message, record.Log, record.Exception, record.MaskedRaw);
        return $"{when} | tenant={record.TenantKey} | client={record.ClientKey} | corr={record.CorrelationKey} | {record.ServiceKey} | {record.LevelKey} | {text[..Math.Min(text.Length, 320)]}";
    }

    private static string BaselineKey(string tenantKey, string clientKey, string processKey) => $"{tenantKey}|{clientKey}|{processKey}";

    private static int SeverityRank(string severity) => severity switch
    {
        "High" => 3,
        "Medium" => 2,
        _ => 1
    };

    private static bool ContainsAny(string? value, params string[] tokens) =>
        !string.IsNullOrWhiteSpace(value) && tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private sealed record CorrelationFlow(
        string TenantKey,
        string ClientKey,
        string ProcessKey,
        string CorrelationId,
        string Signature,
        int RecordCount,
        int ErrorCount,
        double DurationMs,
        IReadOnlyList<LogRecord> Records);
}
