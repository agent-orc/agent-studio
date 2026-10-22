using AgentStudio.Deploy;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: fixed release timestamps only establish relative order and are never compared with now.
public sealed class DeliveryLaneProjectionTests
{
    private static readonly DeployEnvironment[] Environments =
    [
        new("dev", "one", "Dev", 0, "staging"),
        new("stage", "one", "Stage", 1, "staging"),
        new("prod", "one", "Production", 2, "production"),
    ];

    private static Release Release(string id, DateTime at, params string[] cards)
        => new(id, "one", id, id, [], "", "", cards, at, "default");

    private static Deployment Run(string id, string release, string environment, DateTime at)
        => new(id, release, environment, DeploymentStates.Succeeded, at, at, null, [],
            DeploymentTriggers.Deploy, null, at);

    [Fact]
    public void OutOfOrderInputUsesReleaseOrderAndFurthestEnvironment()
    {
        var older = Release("v1", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "A");
        var newer = Release("v2", older.CreatedAtUtc.AddDays(1), "B");
        var projected = DeliveryLaneProjection.Project("one", Environments, [newer, older],
            [Run("d", "v2", "dev", newer.CreatedAtUtc), Run("s", "v1", "stage", older.CreatedAtUtc)],
            new Dictionary<string, string> { ["A"] = "First", ["B"] = "Second", ["C"] = "Waiting" });
        Assert.Equal(["C"], projected.Lanes[0].Cards.Select(card => card.Key));
        Assert.Equal(["B"], projected.Lanes[1].Cards.Select(card => card.Key));
        Assert.Equal(["A"], projected.Lanes[2].Cards.Select(card => card.Key));
        Assert.Empty(projected.Lanes[3].Cards);
        Assert.Equal("v2", projected.Lanes[1].Cards[0].ReleaseVersion);
    }

    [Fact]
    public void RollbackDropsCardToEarlierEnvironmentOrWaiting()
    {
        var older = Release("v1", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "A");
        var newer = Release("v2", older.CreatedAtUtc.AddDays(1), "B");
        var runs = new[] { Run("dev", "v2", "dev", newer.CreatedAtUtc),
            Run("stage", "v2", "stage", newer.CreatedAtUtc),
            Run("rollback", "v1", "stage", newer.CreatedAtUtc.AddDays(1)) };
        var projected = DeliveryLaneProjection.Project("one", Environments, [older, newer], runs,
            new Dictionary<string, string> { ["A"] = "First", ["B"] = "Second" });
        Assert.Equal(["B"], projected.Lanes[1].Cards.Select(card => card.Key));
        Assert.Equal(["A"], projected.Lanes[2].Cards.Select(card => card.Key));
        var fullyRolledBack = DeliveryLaneProjection.Project("one", Environments, [older, newer],
            [.. runs, Run("dev-rollback", "v1", "dev", newer.CreatedAtUtc.AddDays(2))],
            new Dictionary<string, string> { ["A"] = "First" });
        Assert.Equal(["B"], fullyRolledBack.Lanes[0].Cards.Select(card => card.Key));
    }

    [Fact]
    public void ProjectsAndEmptyStatesRemainIsolated()
    {
        var empty = DeliveryLaneProjection.Project("two", [], [], [], new Dictionary<string, string>());
        Assert.Equal("two", empty.ProjectId);
        Assert.Single(empty.Lanes);
        Assert.Empty(empty.Lanes[0].Cards);
        var other = DeliveryLaneProjection.Project("one", Environments, [], [],
            new Dictionary<string, string> { ["ONLY-ONE"] = "One" });
        Assert.Equal("ONLY-ONE", Assert.Single(other.Lanes[0].Cards).Key);
        Assert.Empty(empty.Lanes[0].Cards);
    }
}
