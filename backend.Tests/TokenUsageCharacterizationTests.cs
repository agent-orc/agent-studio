using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: every surface receives the fixed Now; wall-clock FetchedAt fields are not projected.

/// <summary>
/// AGT-3037 golden characterization of the token usage and pricing area.
/// One fixed workspace (bus rows, task receipts, a chat fallback receipt, and
/// a pipeline record) covers display labels, dated and bare aliases, an
/// unknown model, a historical price change, a date before a model's first
/// price, duplicate re-emissions, a receipt residual, and a receipt that
/// overlaps the bus. Every surface the refactor touches is projected to its
/// numbers and compared with <c>Golden/token-usage-characterization.json</c>.
/// The golden file is the contract: a refactor of this area must leave it
/// byte-identical. Regenerate it only for an intended number change, with
/// <c>TOKEN_USAGE_GOLDEN_UPDATE=1</c>.
/// </summary>
public sealed class TokenUsageCharacterizationTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _workspace;
    private readonly string _alpha;
    private readonly string _beta;

    public TokenUsageCharacterizationTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "token-golden-" + Guid.NewGuid().ToString("N"));
        _alpha = Path.Combine(_workspace, "alpha");
        _beta = Path.Combine(_workspace, "beta");
        Directory.CreateDirectory(_alpha);
        Directory.CreateDirectory(_beta);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task TokenUsageSurfaces_MatchTheGoldenNumbers()
    {
        var services = BuildServices();
        await WriteFixtureAsync(services.GetRequiredService<AgentMessageBusStore>());

        var actual = JsonSerializer.Serialize(Project(services), new JsonSerializerOptions { WriteIndented = true })
            .ReplaceLineEndings("\n") + "\n";

        var goldenPath = GoldenPath();
        if (Environment.GetEnvironmentVariable("TOKEN_USAGE_GOLDEN_UPDATE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            File.WriteAllText(goldenPath, actual);
        }

        Assert.True(File.Exists(goldenPath), $"Golden file missing: {goldenPath}");
        var expected = File.ReadAllText(goldenPath).ReplaceLineEndings("\n");
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            var actualPath = Path.Combine(Path.GetTempPath(), "token-usage-characterization.actual.json");
            File.WriteAllText(actualPath, actual);
            Assert.Fail($"Token usage numbers drifted from {goldenPath}. Actual output: {actualPath}");
        }
    }

    private object Project(ServiceProvider services)
    {
        var aggregator = services.GetRequiredService<ITokenAggregator>();
        var projects = new[] { ("alpha", _alpha), ("beta", _beta) };

        var alphaSummary = aggregator.LifetimeSummary("alpha", _alpha);
        var betaSummary = aggregator.LifetimeSummary("beta", _beta);
        var workspace = aggregator.WorkspaceAggregate(projects);
        var perJob = aggregator.WorkspacePerJob("alpha", _alpha);
        var timeline = aggregator.WorkspaceTimeline(projects, 168, 60, Now);
        var history = aggregator.WorkspaceTimelineRange(
            projects, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), Now, 1440);
        var usage = aggregator.ProjectSummary("alpha", _alpha, Now);
        var heatmap = aggregator.ProjectHeatmap("alpha", _alpha, 30, Now);
        var expensive = aggregator.ProjectExpensiveJobs("alpha", _alpha, 10);
        var detail = aggregator.ProjectJobDetail("alpha", _alpha, "AGT-1");

        var ledger = services.GetRequiredService<BusBackedProjectTokenUsageReader>();
        var calendar = UsageCockpitProjection.Calendar(Now, "Etc/UTC", DayOfWeek.Monday);
        var cockpit = UsageCockpitProjection.Cost(calendar,
        [
            new UsageCostInput("alpha", "alpha", ledger.LoadSnapshot("alpha", _alpha).Entries,
                ledger.LoadSnapshot("alpha", _alpha).Freshness),
            new UsageCostInput("beta", "beta", ledger.LoadSnapshot("beta", _beta).Entries,
                ledger.LoadSnapshot("beta", _beta).Freshness),
        ]);

        var adhoc = aggregator.AdHocAggregate();
        var record = PipelineRecord();
        var ledgerCalls = LedgerCalls();
        var sessions = Sessions();

        return new
        {
            alphaSummary = Summary(alphaSummary),
            betaSummary = Summary(betaSummary),
            workspace = new
            {
                workspace.Projects,
                workspace.OrchestratorEntries,
                workspace.OrchestratorLlmCalls,
                workspace.TotalInputTokens,
                workspace.TotalOutputTokens,
                workspace.TotalCacheReadTokens,
                workspace.TotalCacheCreationTokens,
                cost = D(workspace.EstimatedApiCostUsd),
                workspace.AllModelsPriced,
                firstActivity = T(workspace.FirstActivity),
                lastActivity = T(workspace.LastActivity),
                byModel = workspace.ByModel.Select(ByModel).ToList(),
                byProject = workspace.ByProject.Select(p => new
                {
                    p.Project,
                    p.OrchestratorLlmCalls,
                    p.InputTokens,
                    p.OutputTokens,
                    p.CacheReadTokens,
                    p.CacheCreationTokens,
                    cost = D(p.EstimatedApiCostUsd),
                }).ToList(),
            },
            perJob = perJob.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new { job = pair.Key, summary = Task(pair.Value) }).ToList(),
            runModelFallback = new
            {
                noModel = Task(TokenSummaryService.WithModelFallback(perJob["AGT-7"], "claude-opus-5")),
                labelledFallback = Task(TokenSummaryService.WithModelFallback(perJob["AGT-7"], "Claude Sonnet 5")),
                alreadyModelled = Task(TokenSummaryService.WithModelFallback(perJob["AGT-1"], "claude-opus-5")),
            },
            timeline = Timeline(timeline),
            history = Timeline(history),
            projectUsage = new
            {
                usage.HasData,
                usage.LifetimeTotalTokens,
                usage.LifetimeJobTokens,
                usage.LifetimeSupportingTokens,
                usage.LifetimeOrchestratorTokens,
                usage.LifetimeCalls,
                usage.Last24hTotalTokens,
                usage.Last24hJobTokens,
                usage.Last24hSupportingTokens,
                usage.Last24hOrchestratorTokens,
                usage.Last24hCalls,
                usage.Last7dTotalTokens,
                usage.Last7dCalls,
                firstActivity = T(usage.FirstActivity),
                lastActivity = T(usage.LastActivity),
                freshness = new { usage.Freshness.Status, asOf = T(usage.Freshness.AsOf), usage.Freshness.Sources },
            },
            heatmap = new
            {
                heatmap.Days,
                jobs = heatmap.Jobs.Select(job => new
                {
                    job.JobId,
                    job.Category,
                    job.Total,
                    job.Calls,
                    lastActivity = T(job.LastActivity),
                    cells = job.Cells.Select(cell => new { cell.Day, cell.Total }).ToList(),
                }).ToList(),
            },
            expensive = expensive.Select(job => new
            {
                job.JobId,
                job.Category,
                job.TotalTokens,
                job.Calls,
                job.LastModel,
                lastActivity = T(job.LastActivity),
            }).ToList(),
            jobDetail = detail is null ? null : new
            {
                detail.Category,
                detail.TotalTokens,
                detail.InputTokens,
                detail.OutputTokens,
                detail.CacheReadTokens,
                detail.CacheCreationTokens,
                detail.Calls,
                firstActivity = T(detail.FirstActivity),
                lastActivity = T(detail.LastActivity),
                detail.LastModel,
                runs = detail.Runs.Select(run => new
                {
                    run.Index,
                    ts = T(run.Ts),
                    run.Model,
                    run.InputTokens,
                    run.OutputTokens,
                    run.CacheReadTokens,
                    run.CacheCreationTokens,
                    run.Total,
                    run.DeltaVsPrev,
                }).ToList(),
            },
            cockpit = new
            {
                today = D(cockpit.TodayUsd),
                week = D(cockpit.WeekUsd),
                cockpit.Coverage.Status,
                projects = cockpit.Projects.Select(p => new
                {
                    p.Name,
                    today = D(p.TodayUsd),
                    week = D(p.WeekUsd),
                    p.Coverage.Status,
                    p.Coverage.Reason,
                }).ToList(),
            },
            adhoc = new
            {
                adhoc.Calls,
                adhoc.InputTokens,
                adhoc.OutputTokens,
                adhoc.CacheReadTokens,
                adhoc.CacheCreationTokens,
                cost = D(adhoc.EstimatedApiCostUsd),
                adhoc.AllModelsPriced,
                bySource = adhoc.BySource.Select(row => new { row.Source, row.Calls, row.InputTokens, cost = D(row.EstimatedApiCostUsd) }).ToList(),
                byDay = adhoc.ByDay.Select(row => new { row.Date, row.Calls, row.InputTokens, cost = D(row.EstimatedApiCostUsd) }).ToList(),
                byModel = adhoc.ByModel.Select(row => new
                {
                    row.Model,
                    row.ModelId,
                    row.Cli,
                    row.Calls,
                    row.InputTokens,
                    row.OutputTokens,
                    row.CacheReadTokens,
                    row.CacheCreationTokens,
                    cost = D(row.EstimatedApiCostUsd),
                    row.ModelPriced,
                }).ToList(),
            },
            pipeline = new
            {
                summarize = Cost(PipelineCostCalculator.Summarize(record)),
                withLedger = Cost(PipelineCostCalculator.SummarizeWithLedger(record, ledgerCalls)),
                byModel = Usage(PipelineCostCalculator.SummarizeByModel(record)),
                byModelWithSessions = Usage(PipelineCostCalculator.SummarizeByModel(
                    record, sessions, perJob["AGT-2"])),
                decision = Decision(PipelineCostCalculator.SummarizeDecisionCost(record, ledgerCalls)),
            },
        };
    }

    private static object Summary(TokenSummary summary) => new
    {
        summary.Project,
        summary.OrchestratorEntries,
        summary.OrchestratorLlmCalls,
        summary.TotalInputTokens,
        summary.TotalOutputTokens,
        summary.TotalCacheReadTokens,
        summary.TotalCacheCreationTokens,
        cost = D(summary.EstimatedApiCostUsd),
        summary.AllModelsPriced,
        summary.UnknownModelCount,
        firstActivity = T(summary.FirstActivity),
        lastActivity = T(summary.LastActivity),
        byModel = summary.ByModel.Select(ByModel).ToList(),
    };

    private static object ByModel(TokenSummaryByModel row) => new
    {
        row.Model,
        row.ModelId,
        row.Cli,
        row.Calls,
        row.InputTokens,
        row.OutputTokens,
        row.CacheReadTokens,
        row.CacheCreationTokens,
        cost = D(row.EstimatedApiCostUsd),
        row.ModelPriced,
        row.ModelInCatalog,
    };

    private static object Task(TaskTokenSummary summary) => new
    {
        summary.Calls,
        summary.InputTokens,
        summary.OutputTokens,
        summary.CacheReadTokens,
        summary.CacheCreationTokens,
        summary.TotalTokens,
        cost = D(summary.EstimatedApiCostUsd),
        summary.AllModelsPriced,
        summary.LastModel,
        summary.LastModelId,
        summary.LastUpdate,
        summary.HasModelMismatch,
        entries = summary.Entries.Select(call => new
        {
            call.Ts,
            call.Model,
            call.DisplayModel,
            call.ParticipantId,
            call.CliType,
            call.Host,
            call.InputTokens,
            call.OutputTokens,
            call.CacheReadTokens,
            call.CacheCreationTokens,
            call.InputIncludesCached,
            cost = D(call.EstimatedApiCostUsd),
            call.ModelPriced,
        }).ToList(),
    };

    private static object Timeline(TokenTimeline timeline) => new
    {
        windowStart = T(timeline.WindowStart),
        windowEnd = T(timeline.WindowEnd),
        timeline.WindowHours,
        timeline.BucketMinutes,
        timeline.BucketCount,
        freshness = new { timeline.Freshness.Status, asOf = T(timeline.Freshness.AsOf), timeline.Freshness.Sources },
        projects = timeline.Projects.Select(p => new
        {
            p.Project,
            p.Calls,
            p.Input,
            p.Output,
            p.CacheRead,
            p.CacheWrite,
            p.Total,
            dollars = D(p.Dollars),
            p.AllModelsPriced,
            peakBucketStart = T(p.PeakBucketStart),
            p.PeakBucketTotal,
            lastActivity = T(p.LastActivity),
            p.AgentTokens,
            p.SupportingTokens,
            p.OrchestratorTokens,
            hosts = p.Hosts.Select(h => new { h.Host, h.Calls, h.Total }).ToList(),
        }).ToList(),
        cells = timeline.Cells.Select(c => new
        {
            c.Project,
            bucketStart = T(c.BucketStart),
            c.Calls,
            c.Input,
            c.Output,
            c.CacheRead,
            c.CacheWrite,
            c.Total,
            dollars = D(c.Dollars),
            c.AllModelsPriced,
            c.AgentTokens,
            c.SupportingTokens,
            c.OrchestratorTokens,
        }).ToList(),
        models = timeline.Models.Select(m => new
        {
            m.Project,
            m.Model,
            m.ModelLabel,
            m.Host,
            m.CliTypes,
            m.Calls,
            m.Input,
            m.Output,
            m.CacheRead,
            m.CacheWrite,
            m.Total,
            dollars = D(m.Dollars),
            m.AllModelsPriced,
        }).ToList(),
    };

    private static object Cost(PipelineCostSummary cost) => new
    {
        steps = cost.Steps.Select(step => new
        {
            step.StepId,
            step.Model,
            step.TokenUsageSource,
            step.ModelKnown,
            step.PricingGaps,
            step.InputTokens,
            step.OutputTokens,
            step.CacheReadTokens,
            step.CacheCreationTokens,
            step.TotalTokens,
            input = D(step.InputCostUsd),
            output = D(step.OutputCostUsd),
            cacheRead = D(step.CacheReadCostUsd),
            cacheCreation = D(step.CacheCreationCostUsd),
            cost = D(step.CostUsd),
            step.Runs,
        }).ToList(),
        cost.TotalTokens,
        input = D(cost.TotalInputCostUsd),
        output = D(cost.TotalOutputCostUsd),
        cacheRead = D(cost.TotalCacheReadCostUsd),
        cacheCreation = D(cost.TotalCacheCreationCostUsd),
        total = D(cost.TotalCostUsd),
        cost.AnyModelUnknown,
        cost.UnpricedRuns,
        cost.PricingGaps,
    };

    private static object Usage(PipelineModelUsageSummary usage) => new
    {
        runs = usage.Runs.Select(run => new
        {
            run.Attempt,
            run.Current,
            run.TotalTokens,
            cost = D(run.TotalCostUsd),
            run.AnyModelUnknown,
            run.PricingGaps,
            run.TokenUsageAvailable,
            models = run.Models.Select(Model).ToList(),
        }).ToList(),
        totalByModel = usage.TotalByModel.Select(Model).ToList(),
        usage.TotalTokens,
        cost = D(usage.TotalCostUsd),
        usage.AnyModelUnknown,
        usage.UnpricedRuns,
        usage.PricingGaps,
        usage.MissingTokenRuns,
    };

    private static object Decision(DecisionCostRollup rollup) => new
    {
        deciding = Bucket(rollup.Deciding),
        agentRuns = Bucket(rollup.AgentRuns),
        other = Bucket(rollup.Other),
    };

    private static object Bucket(DecisionCostBucket bucket) => new
    {
        bucket.Tokens,
        priced = D(bucket.PricedCostUsd),
        bucket.UnpricedTokens,
        bucket.Runs,
        bucket.UnpricedRuns,
    };

    private static object Model(PipelineModelTokenUsage model) => new
    {
        model.Model,
        model.ThinkingLevel,
        model.ModelKnown,
        model.UnpricedRuns,
        model.PricingGaps,
        model.Steps,
        model.InputTokens,
        model.OutputTokens,
        model.CacheReadTokens,
        model.CacheCreationTokens,
        model.TotalTokens,
        cost = D(model.CostUsd),
    };

    /// <summary>Normalized instant text: the UTC instant, never the host's local offset.</summary>
    private static string? T(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? value
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    /// <summary>Normalized decimal text: the number, never its storage scale.</summary>
    private static string? D(decimal? value)
        => value?.ToString("0.############################", CultureInfo.InvariantCulture);

    private ServiceProvider BuildServices()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchedPaths:0:Name"] = "alpha",
                ["WatchedPaths:0:Path"] = _alpha,
                ["WatchedPaths:1:Name"] = "beta",
                ["WatchedPaths:1:Path"] = _beta,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<AgentMessageBusStore>();
        services.AddSingleton<SummaryGenerationService>();
        services.AddSingleton<TaskScannerService>();
        services.AddSingleton<JobStatsMetadataCache>();
        services.AddSingleton<BusAggregationCache>();
        services.AddSingleton<TokenSummaryCacheStore>();
        services.AddSingleton<TokenPricingDriftMonitor>();
        services.AddSingleton<ProjectTokenReceiptReader>();
        services.AddSingleton<BusBackedProjectTokenUsageReader>();
        services.AddSingleton<BusBackedWorkspaceTimelineReader>();
        services.AddSingleton<BusBackedAdHocUsageReader>();
        services.AddSingleton<ITokenAggregator, TokenAggregationService>();
        return services.BuildServiceProvider();
    }

    private async Task WriteFixtureAsync(AgentMessageBusStore store)
    {
        // alpha: local bus rows.
        await Bus(store, "alpha", "a001", At(10, 12, 9), "agent:claude", "AGT-1",
            Tokens("claude-sonnet-5", 12_000, 3_400, 80_000, 5_000) with { CliType = "claude" });
        // Identical re-emission of a001: the read path collapses it.
        await Bus(store, "alpha", "a002", At(10, 12, 9), "agent:claude", "AGT-1",
            Tokens("claude-sonnet-5", 12_000, 3_400, 80_000, 5_000) with { CliType = "claude" });
        // Historical display label (AGT-2740).
        await Bus(store, "alpha", "a003", At(10, 12, 10, 30), "support:summary-gen", "AGT-1",
            Tokens("Claude Sonnet 5", 2_000, 300));
        // Bare alias.
        await Bus(store, "alpha", "a004", At(10, 12, 11), "orchestrator:alpha", null,
            Tokens("sonnet", 5_000, 800, 1_000));
        // Dated snapshot alias.
        await Bus(store, "alpha", "a005", At(10, 11, 8), "agent:claude", "AGT-1",
            Tokens("claude-haiku-4-5-20251001", 3_000, 500));
        // Unknown model.
        await Bus(store, "alpha", "a006", At(10, 10, 14), "agent:codex", "AGT-4",
            Tokens("zeta-model-9", 7_000, 900));
        // Legacy OpenAI row without input provenance: normalized on read.
        await Bus(store, "alpha", "a007", At(10, 9, 16), "agent:codex", "AGT-4",
            Tokens("gpt-5.5", 50_000, 2_000, 30_000));
        // Historical price change of gpt-5.6-luna on 2026-07-30.
        await Bus(store, "alpha", "a008", At(7, 15, 10), "agent:codex", "AGT-5",
            Tokens("gpt-5.6-luna", 100_000, 20_000, 40_000) with { InputIncludesCached = true });
        await Bus(store, "alpha", "a009", At(8, 15, 10), "agent:codex", "AGT-5",
            Tokens("gpt-5.6-luna", 100_000, 20_000, 40_000) with { InputIncludesCached = true });
        // Before the model's first catalog price (NoPriceForDate).
        await Bus(store, "alpha", "a010", At(9, 1, 10), "agent:claude", "AGT-6",
            Tokens("claude-opus-5-5", 4_000, 1_000));
        // Agent row without a model: the run-model fallback applies.
        await Bus(store, "alpha", "a011", At(10, 12, 13), "agent:claude", "AGT-7",
            Tokens(null, 1_500, 200));
        // Also persisted in the AGT-3 receipt: counted once.
        await Bus(store, "alpha", "a012", At(10, 13, 8, 15), "agent:claude", "AGT-3",
            Tokens("claude-opus-5", 9_000, 1_200, 20_000, 3_000));

        // alpha: durable receipts.
        var remoteCall = Call("agent:remote-runner:att-1", "Claude Opus 5", At(10, 12, 15),
            20_000, 4_000, 150_000, 6_000, host: "agent-runner-01", cli: "claude");
        var reviewCall = Call("support:remote-review:rev-1", "gpt-6-sol", At(10, 12, 16),
            3_000, 400, 7_000, 0, host: "agent-runner-01", cli: "codex") with { InputIncludesCached = true };
        // A pre-AGT-3012 duplicate plus 1,000 residual input tokens not covered by any call.
        Receipt(_alpha, "AGT-2", [remoteCall, remoteCall, reviewCall], residualInput: 1_000,
            lastModel: "claude-opus-5", lastUpdate: At(10, 12, 17));
        Receipt(_alpha, "AGT-3",
            [Call("agent:claude", "claude-opus-5", At(10, 13, 8, 15), 9_000, 1_200, 20_000, 3_000)
                with { EstimatedApiCostUsd = 0.123456m, ModelPriced = true }],
            residualInput: 0, lastModel: "claude-opus-5", lastUpdate: At(10, 13, 8, 15));

        // Workspace-scoped ad-hoc one-shot calls.
        await AdHoc(store, "w001", At(10, 12, 7), "TitleGen", Tokens("claude-haiku-4-5-20251001", 800, 40));
        await AdHoc(store, "w002", At(10, 12, 8), "TitleGen", Tokens("Claude Haiku 4.5", 900, 50));
        await AdHoc(store, "w003", At(10, 11, 9), "SummaryGen", Tokens("sonnet", 4_000, 600, 2_000));
        await AdHoc(store, "w004", At(10, 10, 9), "SummaryGen", Tokens("zeta-model-9", 1_000, 100));
        await AdHoc(store, "w005", At(7, 20, 9), "Triage", Tokens("gpt-5.6-luna", 10_000, 1_000, 3_000));

        // beta.
        await Bus(store, "beta", "b001", At(10, 12, 12), "agent:claude", "BET-1",
            Tokens("claude-sonnet-5", 4_000, 600));
        await Bus(store, "beta", "b002", At(10, 13, 9), "agent:claude", "BET-1",
            Tokens("Claude Sonnet 5", 1_000, 100));
        await Bus(store, "beta", "b003", At(10, 8, 10), "agent:codex", "BET-2",
            Tokens("gpt-5.6-sol", 10_000, 3_000, 5_000) with { InputIncludesCached = true });
        await ChatUsageFallbackReceipts.WriteAsync(_workspace, "beta", "turn-1", At(10, 13, 10),
            new OrchestratorTokenUsage
            {
                Model = "gpt-6-sol",
                InputTokens = 2_000,
                OutputTokens = 100,
                CacheReadTokens = 1_000,
                CliType = "codex",
                Host = "agent-runner-02",
                InputIncludesCached = true,
            });
    }

    private static PipelineExecutionRecord PipelineRecord()
    {
        var started = At(10, 12, 14);
        return new PipelineExecutionRecord
        {
            JobId = "AGT-2",
            Project = "alpha",
            Attempt = 2,
            StartedAt = started,
            Steps =
            [
                Step("coding", StepKind.Core, "claude-opus-5", 20_000, 4_000, 150_000, 6_000, "high"),
                Step("remote-coding", StepKind.Core, "claude-opus-5", 1, 1, 0, 0, "high"),
                Step("review", StepKind.Aspect, "gpt-6-sol", 3_000, 400, 7_000, 0, "medium") with
                {
                    StartedAt = started.AddMinutes(30),
                    EarlierRuns =
                    [
                        new PipelineStepRunSummary
                        {
                            Status = PipelineStepStatus.Passed,
                            StartedAt = started.AddMinutes(10),
                            Model = "gpt-5.5",
                            InputTokens = 8_000,
                            OutputTokens = 900,
                            CacheReadTokens = 2_000,
                        },
                    ],
                },
                Step("label-model", StepKind.Aspect, "Claude Sonnet 5", 1_000, 200, 0, 0, null),
                Step("bare-alias", StepKind.Orchestrator, "sonnet", 2_500, 300, 400, 0, null),
                Step("unknown-model", StepKind.Aspect, "zeta-model-9", 700, 70, 0, 0, null),
                Step("tool", StepKind.Tool, null, 0, 0, 0, 0, null),
            ],
            PreviousAttempts =
            [
                new PipelineExecutionRecord
                {
                    JobId = "AGT-2",
                    Project = "alpha",
                    Attempt = 1,
                    StartedAt = At(8, 1, 9),
                    CompletedAt = At(8, 1, 11),
                    Steps =
                    [
                        Step("coding", StepKind.Core, "gpt-5.6-sol", 60_000, 9_000, 30_000, 0, "high"),
                        Step("review", StepKind.Aspect, "claude-opus-5-5", 5_000, 500, 0, 0, null),
                        Step("dated", StepKind.Aspect, "claude-haiku-4-5-20251001", 4_000, 400, 0, 0, null),
                    ],
                },
            ],
        };
    }

    private static Dictionary<string, IReadOnlyList<TaskTokenCall>> LedgerCalls() => new()
    {
        ["remote-coding"] =
        [
            Call("agent:remote-runner:att-1", "claude-opus-5", At(10, 12, 15), 20_000, 4_000, 150_000, 6_000)
                with { EstimatedApiCostUsd = 0.5m, ModelPriced = true },
            Call("agent:remote-runner:att-1", "Claude Opus 5", At(10, 12, 15, 30), 1_000, 100, 0, 0),
            Call("agent:remote-runner:att-1", "zeta-model-9", At(10, 12, 15, 45), 500, 50, 0, 0)
                with { EstimatedApiCostUsd = 0.01m, ModelPriced = true },
            Call("agent:remote-runner:att-1", "zeta-model-9", At(10, 12, 15, 50), 300, 30, 0, 0),
        ],
    };

    private static List<SessionEvent> Sessions() =>
    [
        new SessionEvent { Ts = At(10, 12, 14), Kind = "start", Model = "claude-opus-5", ThinkingLevel = "high",
            FinishedAt = At(10, 12, 15, 40) },
        new SessionEvent { Ts = At(10, 12, 15, 40), Kind = "start", Model = "sonnet", ThinkingLevel = "medium" },
    ];

    private static PipelineStepExecution Step(
        string id, StepKind kind, string? model, long input, long output, long cacheRead, long cacheCreation,
        string? level) => new()
    {
        StepId = id,
        Kind = kind,
        Model = model,
        ThinkingLevel = level,
        Status = PipelineStepStatus.Passed,
        InputTokens = input,
        OutputTokens = output,
        CacheReadTokens = cacheRead,
        CacheCreationTokens = cacheCreation,
    };

    private static DateTime At(int month, int day, int hour, int minute = 0)
        => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static AgentMessageTokens Tokens(string? model, long input, long output, long cacheRead = 0, long cacheWrite = 0)
        => new(input, output, cacheRead == 0 ? null : cacheRead, cacheWrite == 0 ? null : cacheWrite, model);

    private Task Bus(AgentMessageBusStore store, string project, string id, DateTime at,
        string participant, string? jobId, AgentMessageTokens tokens)
        => store.AppendAsync(_workspace, new AgentMessage
        {
            Id = $"golden-{id}",
            CreatedAt = at,
            ParticipantId = participant,
            Role = "actor",
            Kind = "token-usage",
            Project = project,
            JobId = jobId,
            Topic = "token",
            Summary = "token usage",
            Tokens = tokens,
        });

    private Task AdHoc(AgentMessageBusStore store, string id, DateTime at, string source, AgentMessageTokens tokens)
        => store.AppendAsync(_workspace, new AgentMessage
        {
            Id = $"golden-{id}",
            CreatedAt = at,
            ParticipantId = "support:adhoc",
            Role = "actor",
            Kind = "token-usage",
            Project = AgentMessageBusPaths.WorkspaceScope,
            Topic = source,
            Summary = "ad-hoc usage",
            Tokens = tokens,
        });

    private static TaskTokenCall Call(string participant, string? model, DateTime ts,
        long input, long output, long cacheRead, long cacheWrite, string? host = null, string? cli = null) => new()
    {
        Ts = ts,
        Model = model,
        ParticipantId = participant,
        Host = host,
        CliType = cli,
        InputTokens = input,
        OutputTokens = output,
        CacheReadTokens = cacheRead,
        CacheCreationTokens = cacheWrite,
    };

    private static void Receipt(string watchPath, string taskId, IReadOnlyList<TaskTokenCall> calls,
        long residualInput, string lastModel, DateTime lastUpdate)
    {
        var folder = Path.Combine(watchPath, "tasks", "000", taskId);
        Directory.CreateDirectory(folder);
        var input = calls.Sum(call => call.InputTokens) + residualInput;
        var summary = new TaskTokenSummary
        {
            Calls = calls.Count,
            InputTokens = input,
            OutputTokens = calls.Sum(call => call.OutputTokens),
            CacheReadTokens = calls.Sum(call => call.CacheReadTokens),
            CacheCreationTokens = calls.Sum(call => call.CacheCreationTokens),
            TotalTokens = input + calls.Sum(call => call.OutputTokens + call.CacheReadTokens + call.CacheCreationTokens),
            EstimatedApiCostUsd = calls.Sum(call => call.EstimatedApiCostUsd),
            AllModelsPriced = calls.All(call => call.ModelPriced),
            LastModel = lastModel,
            LastUpdate = lastUpdate,
            Entries = calls.ToList(),
        };
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(new { id = taskId, title = taskId, tokenSummary = summary }));
    }

    private static string GoldenPath([CallerFilePath] string source = "")
        => Path.Combine(Path.GetDirectoryName(source)!, "Golden", "token-usage-characterization.json");
}
