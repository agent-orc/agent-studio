using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tokens;
using Xunit;

namespace AgentStudio.Tests;

public sealed class UsageCockpitContractTests
{
    private static readonly DateTime At = new(2026, 9, 25, 14, 58, 0, DateTimeKind.Utc);
    private static readonly ProjectTokenDataFreshness Complete = new() { Status = "complete" };

    [Fact]
    public void Codex_raw_cache_is_normalized_once_and_priced_by_separate_dimensions()
    {
        var raw = new AgentMessageTokens(1000, 0, CacheRead: 400, Model: "gpt-5.6-sol");
        var normalized = StoredUsageNormalization.Normalize(raw);
        Assert.Equal(600, normalized.Input);
        Assert.Equal(400, normalized.CacheRead);
        Assert.True(normalized.InputIncludesCached);

        var alreadyNormalized = normalized with { Input = 600 };
        Assert.Equal(alreadyNormalized, StoredUsageNormalization.Normalize(alreadyNormalized));

        var calendar = UsageCockpitProjection.Calendar(At, "Europe/Berlin", DayOfWeek.Monday);
        var entry = Entry("run-1", At, (int)normalized.Input, (int)normalized.CacheRead.Value);
        var cost = UsageCockpitProjection.Cost(calendar,
            [new UsageCostInput("PROJ-001", "One", [entry], Complete)]);
        var once = TokenPricing.Estimate("gpt-5.6-sol", 600, 0, 400, 0, At);
        Assert.True(once.ModelKnown);
        Assert.Equal(once.Total, cost.TodayUsd);
        Assert.Equal(once.Total, cost.WeekUsd);
    }

    [Fact]
    public void Project_children_and_unattributed_reconcile_with_active_receipt_once()
    {
        var bus = Entry("active", At, 600, 400);
        var receipt = bus with { Topic = "task-token-receipt" };
        var merged = ProjectTokenReceiptReader.MergeWithoutDuplicates([bus], [receipt]);
        Assert.Single(merged);
        var other = Entry("other", At, 100, 0);
        var unattributed = Entry(null, At, 20, 0);
        var cost = UsageCockpitProjection.Cost(
            UsageCockpitProjection.Calendar(At, "Europe/Berlin", DayOfWeek.Monday),
            [new UsageCostInput("PROJ-001", "One", merged, Complete, At),
             new UsageCostInput("PROJ-002", "Two", [other], Complete),
             new UsageCostInput(null, "Unattributed", [unattributed], Complete)]);
        Assert.Equal(cost.Projects.Sum(project => project.TodayUsd), cost.TodayUsd);
        Assert.Equal(cost.Projects.Sum(project => project.WeekUsd), cost.WeekUsd);
        Assert.Equal(3, cost.Projects.Count);
        Assert.Equal(At, cost.LatestReceiptAt);
        Assert.Equal(TokenPricing.Estimate("gpt-5.6-sol", 600, 0, 400, 0, At).Total,
            cost.Projects[0].TodayUsd);
    }

    [Fact]
    public void Missing_ledger_and_unpriced_model_are_partial_not_zero()
    {
        var calendar = UsageCockpitProjection.Calendar(At, "Etc/UTC", null);
        var unavailable = new UsageCostInput("PROJ-001", "One", [],
            new ProjectTokenDataFreshness { Status = "unavailable" });
        var unpriced = new UsageCostInput("PROJ-002", "Two",
            [new OrchestratorLogEntry { Ts = At, TokenUsage = new OrchestratorTokenUsage
                { Model = "unknown-model", InputTokens = 10 } }], Complete);
        var cost = UsageCockpitProjection.Cost(calendar, [unavailable, unpriced]);
        Assert.Equal("partial", cost.Coverage.Status);
        Assert.Null(cost.Projects[0].TodayUsd);
        Assert.Equal("partial", cost.Projects[1].Coverage.Status);
        Assert.Equal(0m, cost.TodayUsd);
        Assert.Null(UsageCockpitProjection.Cost(calendar, [unavailable]).TodayUsd);
    }

    [Fact]
    public void Local_midnight_dst_and_week_start_follow_workspace_settings()
    {
        var berlin = UsageCockpitProjection.Calendar(
            new DateTime(2026, 3, 29, 12, 0, 0, DateTimeKind.Utc), "Europe/Berlin", DayOfWeek.Sunday);
        Assert.Equal(new DateTime(2026, 3, 28, 23, 0, 0, DateTimeKind.Utc), berlin.DayStartUtc);
        Assert.Equal(new DateTime(2026, 3, 29, 22, 0, 0, DateTimeKind.Utc), berlin.DayEndUtc);
        Assert.Equal(TimeSpan.FromHours(23), berlin.DayEndUtc - berlin.DayStartUtc);
        Assert.Equal(berlin.DayStartUtc, berlin.WeekStartUtc);

        var before = UsageCockpitProjection.Calendar(
            new DateTime(2026, 9, 25, 21, 59, 0, DateTimeKind.Utc), "Europe/Berlin", DayOfWeek.Monday);
        var after = UsageCockpitProjection.Calendar(
            new DateTime(2026, 9, 25, 22, 1, 0, DateTimeKind.Utc), "Europe/Berlin", DayOfWeek.Monday);
        Assert.Equal(before.DayEndUtc, after.DayStartUtc);
        Assert.Equal(before.WeekStartUtc, after.WeekStartUtc);
        var ny = UsageCockpitProjection.Calendar(At, "America/New_York", DayOfWeek.Sunday);
        Assert.NotEqual(before.DayStartUtc, ny.DayStartUtc);
        Assert.NotEqual(before.WeekStartUtc, ny.WeekStartUtc);
    }

    [Fact]
    public void Provider_windows_keep_reset_instants_and_default_cache_ttl()
    {
        var providerReset = new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);
        var snapshot = new QuotaSnapshot { CliType = "codex", FetchedAt = At,
            Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 15.5,
                ResetAt = providerReset }] };
        var projected = UsageCockpitEndpoints.ProjectCli(snapshot, 0, true, At, null, true);
        Assert.Equal(providerReset, projected.Windows[0].ResetAtUtc);
        Assert.Equal(15.5, projected.Windows[0].UsedPct);
        Assert.Equal(600, projected.TtlSeconds);
        Assert.True(projected.Primary);
        Assert.Equal("complete", projected.Availability.Status);
        Assert.Null(UsageCockpitEndpoints.ProjectCli(snapshot, 600, true, At, null, false).Limited);
        var missing = UsageCockpitEndpoints.ProjectCli(new QuotaSnapshot { CliType = "codex" },
            600, true, At, null, true);
        Assert.Equal("unavailable", missing.Availability.Status);
        Assert.Null(missing.FetchedAt);
    }

    [Fact]
    public void Failed_source_is_isolated_and_run_exposes_configured_and_effective_route()
    {
        var failed = UsageCockpitEndpoints.TryRead<object>(() => throw new IOException(), At);
        var healthy = UsageCockpitEndpoints.TryRead(() => new object(), At);
        Assert.Equal("unavailable", failed.State.Status);
        Assert.Equal("complete", healthy.State.Status);

        var task = new TaskInfo { Id = "active", Key = "ONE-1", CliType = "codex",
            Model = "gpt-6-sol", ThinkingLevel = "xhigh",
            QuotaFallback = new QuotaFallbackStatus("claude", "claude-opus-5", "quota") };
        var status = new ProjectRunnerStatus { ActiveJobId = "active",
            ActiveExecution = new CliExecution { JobId = "active", StartedAt = At,
                Model = "claude-opus-5", ThinkingLevel = "high" },
            QuotaFallbackReason = "quota" };
        var project = new ProjectRecord { Id = "PROJ-001" };
        var run = UsageCockpitProjection.Run(project, status, task,
            UsageCockpitProjection.Calendar(At, "Etc/UTC", null), [Entry("active", At, 600, 400)]);
        Assert.Equal("gpt-6-sol", run.ConfiguredModel);
        Assert.Equal("claude-opus-5", run.EffectiveModel);
        Assert.Equal("codex", run.ConfiguredCli);
        Assert.Equal("claude", run.EffectiveCli);
        Assert.Equal("xhigh", run.ConfiguredReasoning);
        Assert.Equal("high", run.EffectiveReasoning);
        Assert.True(run.IncludedInTotals);
        Assert.Equal(TokenPricing.Estimate("gpt-5.6-sol", 600, 0, 400, 0, At).Total,
            run.ProvisionalCostUsd);
    }

    private static OrchestratorLogEntry Entry(string? job, DateTime at, int input, int cacheRead)
        => new() { JobId = job, Ts = at,
            TokenUsage = new OrchestratorTokenUsage { Model = "gpt-5.6-sol",
                InputTokens = input, CacheReadTokens = cacheRead,
                InputIncludesCached = true, UsageNormalization = "openai-input-includes-cached-v1" } };
}
