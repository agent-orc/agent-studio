using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tokens;

public sealed record UsageCalendar(
    string TimeZone, DayOfWeek WeekStart, DateTime DayStartUtc, DateTime DayEndUtc,
    DateTime WeekStartUtc, DateTime WeekEndUtc);

public sealed record UsageSourceState(string Status, DateTime? ObservedAt, int? TtlSeconds, string? Reason = null);

public sealed record UsageProjectCost(string? ProjectId, string Name, decimal? TodayUsd, decimal? WeekUsd,
    UsageSourceState Coverage, DateTime? LatestReceiptAt = null);

public sealed record UsageCostProjection(
    string Currency, UsageCalendar Calendar, decimal? TodayUsd, decimal? WeekUsd,
    IReadOnlyList<UsageProjectCost> Projects, UsageSourceState Coverage,
    string PricingVersion, string NormalizationVersion, string LedgerEndpointTemplate,
    DateTime? LatestReceiptAt = null, decimal? DailyBudgetUsd = null, decimal? WeeklyBudgetUsd = null);

public sealed record UsageCostInput(string? ProjectId, string Name,
    IReadOnlyList<OrchestratorLogEntry> Entries, ProjectTokenDataFreshness Freshness,
    DateTime? LatestReceiptAt = null);

/// <summary>Calendar and accounting rules shared by the endpoint and contract fixtures.</summary>
public static class UsageCockpitProjection
{
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };

    public static UsageRun Run(ProjectRecord project, ProjectRunnerStatus status, TaskInfo? task,
        UsageCalendar calendar, IReadOnlyList<OrchestratorLogEntry> entries, string? observedCli = null)
    {
        var execution = status.ActiveExecution;
        decimal provisional = 0;
        var hasUnpriced = false;
        var receiptCount = 0;
        foreach (var entry in entries.Where(entry => entry.JobId == status.ActiveJobId
            && AsUtc(entry.Ts) >= calendar.DayStartUtc && AsUtc(entry.Ts) < calendar.DayEndUtc
            && (execution is null || AsUtc(entry.Ts) >= AsUtc(execution.StartedAt))))
        {
            var usage = entry.TokenUsage;
            if (usage is null) continue;
            receiptCount++;
            try
            {
                var price = TokenPricing.Estimate(usage.Model, usage.InputTokens, usage.OutputTokens,
                    usage.CacheReadTokens, usage.CacheCreationTokens, AsUtc(entry.Ts));
                if (price.ModelKnown) provisional += price.Total;
                else hasUnpriced = true;
            }
            catch (Exception) { hasUnpriced = true; }
        }
        var runId = task?.Runner?.AttemptId
            ?? $"{project.Id}:{status.ActiveJobId}:{execution?.StartedAt.Ticks ?? 0}";
        return new UsageRun(runId,
            project.Id, status.ActiveJobId ?? "", task?.Key, execution?.StartedAt,
            hasUnpriced || receiptCount == 0 ? null : provisional,
            !hasUnpriced && receiptCount > 0,
            task?.CliType ?? "Unknown", task?.Model ?? "Unknown", task?.ThinkingLevel ?? "Unknown",
            observedCli ?? task?.QuotaFallback?.CliType ?? task?.CliType ?? "Unknown",
            execution?.Model ?? "Unknown", execution?.ThinkingLevel ?? "Unknown",
            status.QuotaFallbackReason ?? task?.QuotaFallback?.Reason);
    }

    public static UsageCalendar Calendar(DateTime nowUtc, string? zoneId, DayOfWeek? weekStart)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(zoneId) ? "Etc/UTC" : zoneId);
        var startDay = weekStart ?? DayOfWeek.Monday;
        if (!Enum.IsDefined(startDay)) throw new ArgumentOutOfRangeException(nameof(weekStart));
        var local = TimeZoneInfo.ConvertTimeFromUtc(AsUtc(nowUtc), zone).Date;
        var daysBack = ((int)local.DayOfWeek - (int)startDay + 7) % 7;
        var week = local.AddDays(-daysBack);
        return new UsageCalendar(zone.Id, startDay,
            ToUtc(local, zone), ToUtc(local.AddDays(1), zone),
            ToUtc(week, zone), ToUtc(week.AddDays(7), zone));
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(wall)) wall = wall.AddMinutes(1);
        if (zone.IsAmbiguousTime(wall))
        {
            // The first occurrence owns the start of an ambiguous local day.
            return new DateTimeOffset(wall, zone.GetAmbiguousTimeOffsets(wall).Max()).UtcDateTime;
        }
        return TimeZoneInfo.ConvertTimeToUtc(wall, zone);
    }

    public static UsageCostProjection Cost(UsageCalendar calendar, IReadOnlyList<UsageCostInput> sources)
    {
        var children = new List<UsageProjectCost>(sources.Count);
        foreach (var source in sources)
        {
            var available = source.Freshness.Status != "unavailable";
            decimal today = 0, week = 0;
            var unpriced = false;
            var legacyUnknown = false;
            DateTime? newest = null;
            foreach (var entry in source.Entries)
            {
                var at = AsUtc(entry.Ts);
                if (at < calendar.WeekStartUtc || at >= calendar.WeekEndUtc) continue;
                var usage = entry.TokenUsage;
                if (usage is null) continue;
                newest = newest is null || at > newest ? at : newest;
                // The merged reader already normalized legacy bus rows and durable
                // receipts. Subtracting cache here would charge it twice.
                TokenCostEstimate estimate;
                try
                {
                    estimate = TokenPricing.Estimate(usage.Model, usage.InputTokens,
                        usage.OutputTokens, usage.CacheReadTokens, usage.CacheCreationTokens, at);
                }
                catch (Exception) { unpriced = true; continue; }
                if (!estimate.ModelKnown) { unpriced = true; continue; }
                if (ProviderUsageNormalization.IsOpenAiModel(usage.Model)
                    && usage.CacheReadTokens > 0 && usage.InputIncludesCached is null)
                    legacyUnknown = true;
                week += estimate.Total;
                if (at >= calendar.DayStartUtc && at < calendar.DayEndUtc) today += estimate.Total;
            }
            var status = !available ? "unavailable"
                : source.Freshness.Status == "partial" || unpriced || legacyUnknown ? "partial" : "complete";
            var reason = string.Join(" ", new[] {
                source.Freshness.Warning,
                unpriced ? "Some models have no historical USD price." : null,
                legacyUnknown ? "Some legacy OpenAI receipts lack normalization provenance." : null
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var sourceAsOf = DateTime.TryParse(source.Freshness.AsOf, out var parsedAsOf)
                ? AsUtc(parsedAsOf) : (DateTime?)null;
            var observedAt = sourceAsOf is null ? newest
                : newest is null ? sourceAsOf : sourceAsOf.Value > newest.Value ? sourceAsOf : newest;
            children.Add(new UsageProjectCost(source.ProjectId, source.Name,
                available ? today : null, available ? week : null,
                new UsageSourceState(status, observedAt,
                    source.ProjectId is null ? 0 : BusBackedProjectTokenUsageReader.SnapshotTtlSeconds,
                    reason.Length == 0 ? null : reason),
                source.LatestReceiptAt is { } receiptAt ? AsUtc(receiptAt) : null));
        }
        var known = children.Where(child => child.TodayUsd is not null || child.WeekUsd is not null).ToList();
        var overall = known.Count == 0 ? "unavailable"
            : children.Any(child => child.Coverage.Status != "complete") ? "partial" : "complete";
        return new UsageCostProjection("USD", calendar,
            known.Count == 0 ? null : known.Sum(child => child.TodayUsd ?? 0m),
            known.Count == 0 ? null : known.Sum(child => child.WeekUsd ?? 0m),
            children, new UsageSourceState(overall,
                children.Select(child => child.Coverage.ObservedAt).Max(),
                BusBackedProjectTokenUsageReader.SnapshotTtlSeconds,
                overall == "complete" ? null : "Known totals may omit unavailable or unpriced usage."),
            $"TokenEconomy/{typeof(TokenEconomy.ModelPriceCatalog).Assembly.GetName().Version?.ToString() ?? "unknown"}",
            ProviderUsageNormalization.OpenAiInputIncludesCachedV1,
            "/api/projects/{project}/token-usage/summary",
            LatestReceiptAt: children.Select(child => child.LatestReceiptAt).Max());
    }
}
