using System.Text.Json;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class MechanicalRoundLedgerTests
{
    [Fact]
    public void Rebase_fresh_fallback_uses_policy_floor_unless_operator_pinned()
    {
        var policy = new ModelRoutingPolicyRegistry();
        var unpinned = LeaseEndpoints.MechanicalFreshRoute(
            new TaskInfo { ModelExplicit = false }, "codex", "gpt-5.6-luna", "low", policy);
        var floor = policy.Policy.Tiers.Single(tier => tier.Id == "sol-xhigh");
        Assert.Equal(floor.Model, unpinned.Model);
        Assert.Equal(floor.ThinkingLevel, unpinned.ThinkingLevel);
        Assert.Equal("semantic-conflict-floor:sol-xhigh", unpinned.Reason);

        var pinned = LeaseEndpoints.MechanicalFreshRoute(
            new TaskInfo { ModelExplicit = true }, "codex", "gpt-5.6-terra", "medium", policy);
        Assert.Equal("gpt-5.6-terra", pinned.Model);
        Assert.Equal("medium", pinned.ThinkingLevel);
        Assert.Equal("operator-pin", pinned.Reason);
    }

    [Theory]
    [InlineData("source-needs-rebase", true)]
    [InlineData("acceptance-rail:source-needs-rebase:retry-1", true)]
    [InlineData("merge-conflict", false)]
    [InlineData("delivery-attribution-ambiguous", false)]
    [InlineData(null, false)]
    public void Only_typed_rebase_recovery_enters_the_session_pilot(string? reason, bool expected)
        => Assert.Equal(expected, LeaseEndpoints.IsRebaseOnlyIntent(reason));

    [Fact]
    public void Completion_replay_keeps_one_task_local_receipt_with_session_and_usage()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mechanical-ledger-" + Guid.NewGuid().ToString("N"));
        try
        {
            var receipt = new MechanicalRoundReceiptDto(
                "11111111-2222-3333-4444-555555555555",
                "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "resumed", null, "codex", "AGT-1", "project-1",
                "https://example.test/repo.git", "/host/worktrees/AGT-1",
                "runner/host/AGT-1", new string('a', 40),
                100, 10, 70, 12.5);
            Assert.True(MechanicalRoundLedger.Append(folder, "attempt-1", "done", new string('b', 40), receipt));
            Assert.True(MechanicalRoundLedger.Append(folder, "attempt-1", "done", new string('b', 40), receipt));

            var path = Path.Combine(folder, "logs", MechanicalRoundLedger.FileName);
            var lines = File.ReadAllLines(path);
            Assert.Single(lines);
            var row = JsonSerializer.Deserialize<MechanicalRoundLedgerEntry>(lines[0],
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal("attempt-1", row!.AttemptId);
            Assert.Equal(receipt.CapturedSessionId, row.Receipt.CapturedSessionId);
            Assert.Equal(100, row.Receipt.InputTokens);
            Assert.Equal(12.5, row.Receipt.DurationSeconds);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
