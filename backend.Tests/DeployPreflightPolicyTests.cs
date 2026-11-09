using AgentStudio.Operator;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: each pure policy call receives a fixed fixture instant.
public sealed class DeployPreflightPolicyTests
{
    [Fact]
    public void Pending_push_blocks_restart_until_it_clears()
    {
        var now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        var pending = new DeployPreflightFacts(
            [new DeployPushFact("sample", "card-1", now.AddMinutes(-2))], [], []);
        var blocked = DeployPreflightPolicy.Check(pending, now);
        Assert.Contains(blocked, item => item.Code == "pending-push" && item.Project == "sample");
        Assert.Empty(DeployPreflightPolicy.Check(pending with { PendingPushes = [] }, now));
    }

    [Fact]
    public void Publishing_flight_and_mid_commit_are_named_blockers()
    {
        var facts = new DeployPreflightFacts([], [new DeployFlightFact("sample", "flight-1", "publishing")],
            [new DeployCommitFact("sample", "card-1", "post-merge-into-develop")]);
        var blockers = DeployPreflightPolicy.Check(facts,
            new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        Assert.Contains(blockers, item => item.Code == "flight-publishing");
        Assert.Contains(blockers, item => item.Code == "card-mid-commit");
    }

    [Fact]
    public void Plan_is_server_first_and_expires_at_the_window_end()
    {
        var now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        var plan = DeployPlanPolicy.Create(now, TimeSpan.FromMinutes(30), "1.0.0", "1.1.0", 2,
            [new DeployHostVersion("host-1", "1.0.0")]);
        Assert.Equal(["drain", "task-server", "readiness", "engine", "hosts"],
            plan.Steps.Select(step => step.Kind));
        Assert.Equal("1.0.0", plan.CurrentVersion);
        Assert.Equal("1.1.0", plan.TargetVersion);
        Assert.False(DeployPlanPolicy.IsExpired(plan, now.AddMinutes(29)));
        Assert.True(DeployPlanPolicy.IsExpired(plan, now.AddMinutes(30)));
    }
}
