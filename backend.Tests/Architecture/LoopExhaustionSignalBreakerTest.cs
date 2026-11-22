using AgentStudio.OrchestrationDecisions;
using System.Text.RegularExpressions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>Every registered recovery loop closes repeat admission before a decision is available.</summary>
[Trait("Category", "Guard")]
public sealed class LoopExhaustionSignalBreakerTest
{
    [Fact]
    public void Inventory_has_thirteen_stable_signals_with_a_breaker_and_no_counter_selected_action()
    {
        var path = Path.Combine(Architecture.CSharpSourceScanner.RepoRoot(), "docs", "system", "contracts", "loop-inventory.md");
        var inventory = File.ReadAllText(path);
        var recovery = inventory.Split("\n## Observation schedulers", 2, StringSplitOptions.None)[0];
        var entries = Regex.Matches(recovery, @"(?m)^### (?<id>[a-z][a-z0-9.-]+)\s*$")
            .Select(match => match.Groups["id"].Value).ToArray();
        Assert.Equal(13, entries.Length);
        Assert.Equal(13, entries.Distinct(StringComparer.Ordinal).Count());
        foreach (var id in entries)
        {
            var section = recovery.Split("### " + id + "\n", 2, StringSplitOptions.None)[1]
                .Split("\n### ", 2, StringSplitOptions.None)[0];
            Assert.Contains($"**Signal raised:** `loop.{id}.exhausted`", section);
            Assert.Contains("**Breaker test:**", section);
            Assert.Contains("Re-entry is blocked", section);
            Assert.DoesNotContain("**Action when budget exhausted:**", section);
        }
    }

    [Fact]
    public void Observation_scheduler_keeps_attention_separate_from_recovery_exhaustion()
    {
        var root = Architecture.CSharpSourceScanner.RepoRoot();
        var inventory = File.ReadAllText(Path.Combine(root, "docs", "system", "contracts", "loop-inventory.md"));
        var sections = inventory.Split("\n## Observation schedulers", 2, StringSplitOptions.None);
        Assert.Equal(2, sections.Length);
        var entries = Regex.Matches(sections[1], @"(?m)^### (?<id>[a-z][a-z0-9.-]+)\s*$")
            .Select(match => match.Groups["id"].Value).ToArray();
        Assert.Equal(["post-merge.host-poll"], entries);
        Assert.Contains("**Budget:**", sections[1]);
        Assert.Contains("**Signal raised:** `pendingWindow.starved`", sections[1]);
        Assert.Contains("**Breaker test:** `backend.Tests/Architecture/PostMergeWindowBudgetTests.cs`", sections[1]);
        Assert.True(File.Exists(Path.Combine(root, "backend.Tests", "Architecture", "PostMergeWindowBudgetTests.cs")));
        Assert.DoesNotContain("loop.post-merge.host-poll.exhausted", sections[1]);
    }

    [Theory]
    [InlineData("pickup.silent-runs-per-job")]
    [InlineData("pickup.cross-slug-infra-circuit-breaker")]
    [InlineData("pickup.diagnose-once-per-dead-letter")]
    [InlineData("pickup.requeue-after-diagnosis")]
    [InlineData("abort-review.rerun-per-job")]
    [InlineData("completion.retrigger-transient-abort-per-job")]
    [InlineData("integration.attribution-agent-round")]
    [InlineData("completion.run-timeout-salvage-continuation")]
    [InlineData("ui-task.human-feedback-iterations")]
    [InlineData("ui-task.visual-defect-auto-retry")]
    [InlineData("clean-context.retention-sweep")]
    [InlineData("local-cli.missing-npm-shim-repair")]
    [InlineData("remote-claim.environment-preparation-per-task")]
    public void Exhaustion_raises_a_named_signal_and_blocks_reentry_without_choosing_an_action(string id)
    {
        Assert.Null(LoopExhaustionPolicy.Signal(id, 1, 2, "per-attempt", "attempt-1", ["retry", "ask"]));
        var signal = LoopExhaustionPolicy.Signal(id, 2, 2, "per-attempt", "attempt-1", ["retry", "ask"]);
        Assert.NotNull(signal);
        Assert.Equal($"loop.{id}.exhausted", signal.Name);
        Assert.True(signal.ReentryBlocked);
        Assert.Equal(["retry", "ask"], signal.CandidateActions);
        Assert.Equal(2, signal.MeasuredCount);
        Assert.Equal(2, signal.Limit);
    }

    [Fact]
    public void Diagnosis_and_requeue_entries_remain_unimplemented()
    {
        var path = Path.Combine(Architecture.CSharpSourceScanner.RepoRoot(), "docs", "system",
            "contracts", "loop-inventory.md");
        var inventory = File.ReadAllText(path);
        foreach (var id in new[] { "pickup.diagnose-once-per-dead-letter",
            "pickup.requeue-after-diagnosis" })
        {
            var section = inventory.Split("### " + id + "\n", 2, StringSplitOptions.None)[1]
                .Split("\n### ", 2, StringSplitOptions.None)[0];
            Assert.Contains("**Last fired:** Not yet implemented.", section);
            Assert.Contains("feature implementation is still pending", section);
        }
    }
}
