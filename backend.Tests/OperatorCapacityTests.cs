using AgentStudio.Cli;
using AgentStudio.Operator;
using AgentStudio.Projects;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class OperatorCapacityTests
{
    [Fact]
    public void Exhausted_cli_active_fallback_wait_and_pin_share_one_response()
    {
        // clock-independent: every freshness and reset comparison uses this fixed value passed to Build.
        var now = new DateTime(2030, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        var report = new QuotaReport { TtlSeconds = 600, Snapshots =
        [
            new QuotaSnapshot { CliType = "claude", FetchedAt = now.AddMinutes(-1),
                Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 99, ResetAt = now.AddDays(1) }] },
            new QuotaSnapshot { CliType = "codex", FetchedAt = now.AddMinutes(-1) },
        ] };
        var tasks = new TaskInfo[]
        {
            new() { Id = "run-1", Key = "SAMPLE-1", ProjectName = "sample", State = TaskStates.Progress,
                QuotaFallback = new QuotaFallbackStatus("codex", "gpt-6-sol", "weekly cap", now) },
            new() { Id = "run-2", Key = "SAMPLE-2", ProjectName = "sample", State = TaskStates.Ready,
                QuotaWait = new QuotaWaitStatus("claude", now, now.AddDays(1), 20, "hard pin") },
        };
        var active = new ModelSelectionEvent[]
        {
            new() { Kind = "switched", Consumer = "coding", Project = "sample", TaskKey = "SAMPLE-1",
                From = new ModelSelectionRoute("claude", "claude-opus-5", "high"),
                To = new ModelSelectionRoute("codex", "gpt-6-sol", "high"), Reason = "weekly cap" },
        };
        var settings = new Dictionary<string, ProjectSettings>
        {
            ["sample"] = new() { PipelineSteps = new() { ["post-review"] = new PipelineStepSetting
                { CliType = "claude", Model = "claude-opus-5", PinMode = "hard" } } },
        };

        var response = OperatorCapacityEndpoints.Build(report, tasks, active, new ModelSelectionPolicy(),
            settings, task => task.QuotaFallback,
            snapshot => snapshot.CliType == "claude" && snapshot.Windows.Any(window => window.UsedPct >= 95), now);

        Assert.True(Assert.Single(response.Quota, row => row.CliType == "claude").Exhausted);
        Assert.True(Assert.Single(response.Quota, row => row.CliType == "claude").Fresh);
        var fallback = Assert.Single(response.ActiveFallbacks);
        Assert.Equal(("SAMPLE-1", "claude-opus-5", "gpt-6-sol", "weekly cap"),
            (fallback.CardKey, fallback.SourceModel, fallback.TargetModel, fallback.Reason));
        Assert.Equal("consumers.coding", fallback.SettingKey);
        var wait = Assert.Single(response.WaitingCards);
        Assert.Equal(("SAMPLE-2", 20, "hard pin"),
            (wait.CardKey, wait.WaitPolicy.ThresholdMinutes, wait.Reason));
        Assert.Equal("hard", Assert.Single(response.ModelRoutingPins).PinMode);
    }

    [Fact]
    public void Ready_cards_that_would_fall_back_or_wait_are_queued_cards()
    {
        // clock-independent: every reset instant derives from this fixed value passed to Build.
        var now = new DateTime(2030, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        var configured = new SelectionRoute("claude", "claude-opus-5", "high");
        var tasks = new TaskInfo[]
        {
            new() { Id = "a", Key = "SAMPLE-3", Title = "Falls back", ProjectName = "sample", State = TaskStates.Ready },
            new() { Id = "b", Key = "SAMPLE-4", Title = "Waits", ProjectName = "sample", State = TaskStates.Ready },
            new() { Id = "c", Key = "SAMPLE-5", Title = "Runs as configured", ProjectName = "sample", State = TaskStates.Ready },
            new() { Id = "d", Key = "SAMPLE-6", Title = "Already running", ProjectName = "sample", State = TaskStates.Progress },
        };
        CapacityForecast Plan(QuotaAdmissionOutcome outcome, string cli, string model, string reason)
            => new(configured, new QuotaAdmissionPlan(outcome, cli, model, "high",
                outcome == QuotaAdmissionOutcome.LaunchFallback, reason, now.AddHours(3), null));
        var forecasts = new Dictionary<string, CapacityForecast>
        {
            ["a"] = Plan(QuotaAdmissionOutcome.LaunchFallback, "codex", "gpt-6-sol", "weekly window at 98%"),
            ["b"] = Plan(QuotaAdmissionOutcome.Wait, "claude", "claude-opus-5", "hard pin waits for reset"),
            ["c"] = Plan(QuotaAdmissionOutcome.LaunchPrimary, "claude", "claude-opus-5", "healthy"),
            ["d"] = Plan(QuotaAdmissionOutcome.LaunchFallback, "codex", "gpt-6-sol", "not queued"),
        };

        var response = OperatorCapacityEndpoints.Build(new QuotaReport(), tasks, [], new ModelSelectionPolicy(),
            new Dictionary<string, ProjectSettings>(), _ => null, _ => false, now,
            task => forecasts[task.Id]);

        Assert.Collection(response.QueuedCards,
            row => Assert.Equal(("SAMPLE-3", "fallback", "claude-opus-5", "gpt-6-sol", "weekly window at 98%"),
                (row.CardKey, row.Outcome, row.ConfiguredModel, row.TargetModel, row.Reason)),
            row => Assert.Equal(("SAMPLE-4", "wait", "claude-opus-5", now.AddHours(3)),
                (row.CardKey, row.Outcome, row.TargetModel, row.NextResetAt)));
    }

    [Fact]
    public void Capacity_forecast_previews_the_launch_without_a_ledger_receipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "capacity-forecast-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = new DateTime(2030, 11, 5, 12, 0, 0, DateTimeKind.Utc);
            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["TaskRepository"] = root }).Build();
            var cache = new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance);
            cache.Write([
                new QuotaSnapshot { CliType = CliTypes.Claude, FetchedAt = now,
                    Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 98, ResetAt = now.AddDays(3) }] },
                new QuotaSnapshot { CliType = CliTypes.Codex, FetchedAt = now,
                    Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 10, ResetAt = now.AddDays(3) }] },
            ]);
            var fallback = new CliQuotaFallbackService(config, NullLogger<CliQuotaFallbackService>.Instance);
            fallback.Set(new CliModelRouteProfile
            {
                CliType = CliTypes.Claude, PrimaryModel = ModelIds.ClaudeOpus5,
                FallbackCliType = CliTypes.Codex, FallbackModel = ModelIds.Gpt56Sol, FallbackThinkingLevel = "high",
            });
            var ledger = new CountingLedger();
            var selection = new ModelSelectionService(
                new QuotaService(NullLogger<QuotaService>.Instance, [], config, cache),
                new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config), fallback,
                new CliQuotaWaitPolicyService(NullLogger<CliQuotaWaitPolicyService>.Instance, config),
                new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config),
                timeProvider: new FixedClock(now), ledger: ledger);
            var card = new TaskInfo
            {
                Id = "ready-1", Key = "SAMPLE-7", ProjectName = "sample", State = TaskStates.Ready,
                FolderPath = root, CliType = CliTypes.Claude, Model = ModelIds.ClaudeOpus5,
                ThinkingLevel = "high", ModelExplicit = true,
            };

            var forecast = OperatorCapacityEndpoints.Forecast(selection, null, card, [card], now);

            Assert.Equal(QuotaAdmissionOutcome.LaunchFallback, forecast.Plan.Outcome);
            Assert.Equal((CliTypes.Codex, ModelIds.Gpt56Sol), (forecast.Plan.CliType, forecast.Plan.Model));
            Assert.Equal(ModelIds.ClaudeOpus5, forecast.Configured.Model);
            Assert.Equal(0, ledger.Records);
            // The same question as a real launch writes the switch receipt, so the zero above is the preview.
            selection.Select(CodingSelectionCalls.For(card, "sample", 0, now, null));
            Assert.Equal(1, ledger.Records);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CountingLedger : IModelSelectionLedger
    {
        public int Records { get; private set; }
        public void Record(SelectionLedgerEntry entry) => Records++;
    }

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }
}
