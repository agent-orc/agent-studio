using AgentStudio.Cli;
using AgentStudio.Operator;
using AgentStudio.Shared;
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
}
