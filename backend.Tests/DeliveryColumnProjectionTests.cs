using AgentStudio.Deploy;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: fixed release and run timestamps only establish relative order and are never compared with now.
public sealed class DeliveryColumnProjectionTests
{
    private static readonly DateTime At = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DeployEnvironment Dev = new("dev", "demo", "Dev", 0, EnvironmentKinds.Other);
    private static readonly DeployEnvironment Stage = new("stage", "demo", "Stage", 1, EnvironmentKinds.Staging);
    private static readonly DeployEnvironment Production = new("production", "demo", "Production", 2, EnvironmentKinds.Production);
    private static readonly DeployEnvironment[] Environments = [Production, Dev, Stage];
    private static readonly IReadOnlyList<ProjectLifecyclePhase> Standard =
    [
        new("dev", true, [], null),
        new("staging", true, [], null),
        new("production", false, ["Owner", "Maintainer"], "staging"),
    ];
    private static readonly Release R1 = Release("r1", 0, "DEMO-1");
    private static readonly Release R2 = Release("r2", 1, "DEMO-2", "DEMO-3");

    private static Release Release(string id, int day, params string[] cards)
        => new(id, "demo", "v" + id, new string(id[^1], 40), [], "", "", cards, At.AddDays(day), "default");

    private static Deployment Run(string id, Release release, DeployEnvironment environment, int hour,
        string state = DeploymentStates.Succeeded, string trigger = DeploymentTriggers.Deploy)
        => new(id, release.Id, environment.Id, state, At.AddDays(10).AddHours(hour),
            DeploymentStates.IsFinished(state) ? At.AddDays(10).AddHours(hour) : null, null, [], trigger, "fixture",
            At.AddDays(10).AddHours(hour));

    private static DeliveryColumnsResponse Project(IReadOnlyList<Release> releases, IReadOnlyList<Deployment> runs,
        IEnumerable<string>? integrated = null, int depth = DeliveryColumnProjection.DefaultReleaseDepth,
        IReadOnlyDictionary<string, IReadOnlyList<ProjectLifecyclePhase>>? lifecycles = null)
        => DeliveryColumnProjection.Project("demo", Environments, releases, runs,
            new Dictionary<string, string> { ["production"] = "healthy" },
            lifecycles ?? releases.ToDictionary(item => item.Id, _ => Standard), integrated ?? [], depth);

    private static DeliveryColumn Column(DeliveryColumnsResponse response, string id)
        => response.Columns.Single(item => item.Id == id);

    private static string[] Keys(IEnumerable<DeliveryColumnCard> cards) => cards.Select(item => item.Key).ToArray();

    private static readonly Deployment[] R1Everywhere =
        [Run("d1", R1, Dev, 0), Run("s1", R1, Stage, 1), Run("p1", R1, Production, 2)];

    [Fact]
    public void Columns_FollowTheLifecycleOrder_AfterTheIntegratedColumn()
    {
        var projected = Project([R1], R1Everywhere);

        Assert.Equal(["integrated", "dev", "stage", "production"], projected.Columns.Select(item => item.Id));
        Assert.Equal([null, EnvironmentKinds.Other, EnvironmentKinds.Staging, EnvironmentKinds.Production],
            projected.Columns.Select(item => item.Kind));
        var production = Column(projected, "production");
        Assert.Equal(("r1", "vr1"), (production.CurrentRelease!.Id, production.CurrentRelease.Version));
        Assert.Equal(("p1", DeploymentStates.Succeeded, At.AddDays(10).AddHours(2)),
            (production.LastRun!.Id, production.LastRun.State, production.LastRun.AtUtc));
        Assert.Equal(EnvironmentHealth.Healthy, production.Health);
        Assert.Equal(EnvironmentHealth.Unknown, Column(projected, "dev").Health);
    }

    [Fact]
    public void Promotion_MovesCardsFromWaitingToLanded_ColumnByColumn()
    {
        var released = Project([R1, R2], R1Everywhere);
        Assert.Equal(["DEMO-2", "DEMO-3"], Keys(Column(released, "dev").Waiting));
        Assert.Empty(Column(released, "stage").Waiting);
        Assert.Equal(["DEMO-1"], Keys(Column(released, "dev").Landed));

        var onDev = Project([R1, R2], [.. R1Everywhere, Run("d2", R2, Dev, 3)]);
        Assert.Equal(["DEMO-2", "DEMO-3", "DEMO-1"], Keys(Column(onDev, "dev").Landed));
        Assert.Equal(["r2", "r2", "r1"], Column(onDev, "dev").Landed.Select(item => item.ReleaseId));
        Assert.Empty(Column(onDev, "dev").Waiting);
        Assert.Equal(["DEMO-2", "DEMO-3"], Keys(Column(onDev, "stage").Waiting));
        Assert.Empty(Column(onDev, "production").Waiting);

        var awaiting = Project([R1, R2], [.. R1Everywhere, Run("d2", R2, Dev, 3), Run("s2", R2, Stage, 4),
            Run("p2", R2, Production, 5, DeploymentStates.AwaitingApproval)]);
        var production = Column(awaiting, "production");
        Assert.Equal(["DEMO-2", "DEMO-3"], Keys(production.Waiting));
        Assert.Equal(["DEMO-1"], Keys(production.Landed));
        Assert.Equal(("p2", "r2", "vr2"), (production.PendingApproval!.DeploymentId,
            production.PendingApproval.ReleaseId, production.PendingApproval.ReleaseVersion));
        Assert.Null(Column(awaiting, "stage").PendingApproval);

        var promoted = Project([R1, R2], [.. R1Everywhere, Run("d2", R2, Dev, 3), Run("s2", R2, Stage, 4),
            Run("p2", R2, Production, 5)]);
        Assert.All(promoted.Columns.Skip(1), column =>
        {
            Assert.Equal(["DEMO-2", "DEMO-3", "DEMO-1"], Keys(column.Landed));
            Assert.Empty(column.Waiting);
            Assert.Null(column.PendingApproval);
        });
    }

    [Fact]
    public void Rollback_MovesTheWithdrawnCardsBackToWaiting_AndMarksThemRolledBack()
    {
        Deployment[] promoted = [.. R1Everywhere, Run("d2", R2, Dev, 3), Run("s2", R2, Stage, 4),
            Run("p2", R2, Production, 5)];

        var projected = Project([R1, R2], [.. promoted,
            Run("p3", R1, Production, 6, trigger: DeploymentTriggers.Rollback)]);

        var production = Column(projected, "production");
        Assert.Equal("r1", production.CurrentRelease!.Id);
        Assert.Equal(["DEMO-1"], Keys(production.Landed));
        Assert.All(production.Landed, card => Assert.False(card.RolledBack));
        Assert.Equal(["DEMO-2", "DEMO-3"], Keys(production.Waiting));
        Assert.All(production.Waiting, card => Assert.True(card.RolledBack));
        Assert.Equal(["DEMO-2", "DEMO-3"], Keys(production.RolledBack));
        Assert.All(production.RolledBack, card => Assert.Equal(("r2", "vr2"), (card.ReleaseId, card.ReleaseVersion)));
        // Stage keeps the release; the rollback is local to Production.
        Assert.Equal(["DEMO-2", "DEMO-3", "DEMO-1"], Keys(Column(projected, "stage").Landed));
        Assert.Empty(Column(projected, "stage").RolledBack);

        var redeployed = Project([R1, R2], [.. promoted,
            Run("p3", R1, Production, 6, trigger: DeploymentTriggers.Rollback), Run("p4", R2, Production, 7)]);
        Assert.Empty(Column(redeployed, "production").RolledBack);
        Assert.Equal(["DEMO-2", "DEMO-3", "DEMO-1"], Keys(Column(redeployed, "production").Landed));
    }

    [Fact]
    public void HotfixLifecycleSkippingStage_ShowsItsCardsLandedInProductionOnly()
    {
        var hotfix = Release("h3", 2, "DEMO-9");
        Deployment[] runs = [.. R1Everywhere, Run("d2", R2, Dev, 3), Run("s2", R2, Stage, 4),
            Run("p2", R2, Production, 5), Run("p3", hotfix, Production, 6)];
        var lifecycles = new Dictionary<string, IReadOnlyList<ProjectLifecyclePhase>>
        {
            ["r1"] = Standard, ["r2"] = Standard,
            ["h3"] = [new("production", false, ["Owner", "Maintainer"], null)],
        };

        var projected = Project([R1, R2, hotfix], runs, lifecycles: lifecycles);

        var production = Column(projected, "production");
        Assert.Equal("h3", production.CurrentRelease!.Id);
        Assert.Equal(["DEMO-9", "DEMO-2", "DEMO-3", "DEMO-1"], Keys(production.Landed));
        foreach (var id in new[] { "dev", "stage" })
        {
            var column = Column(projected, id);
            Assert.Equal("r2", column.CurrentRelease!.Id);
            Assert.DoesNotContain("DEMO-9", Keys(column.Landed));
            Assert.DoesNotContain("DEMO-9", Keys(column.Waiting));
        }
    }

    [Fact]
    public void ArchivedAndUnreleasedCards_StayVisible_AndTheIntegratedColumnHoldsOnlyUnreleasedOnes()
    {
        // DEMO-1 is archived: the board lists no live task for it, the release still names it.
        var projected = Project([R1], R1Everywhere, integrated: ["DEMO-1", "DEMO-7", "ARCH-4", "demo-7"]);

        Assert.Equal(["ARCH-4", "DEMO-7"], Keys(Column(projected, "integrated").Landed));
        Assert.All(Column(projected, "integrated").Landed, card => Assert.Null(card.ReleaseId));
        Assert.All(projected.Columns.Skip(1), column => Assert.Equal(["DEMO-1"], Keys(column.Landed)));
    }

    [Fact]
    public void Landed_IsBoundedToTheNewestReleasesUpToTheCurrentOne()
    {
        var releases = Enumerable.Range(1, 8).Select(day => Release("x" + day, day, "CARD-" + day)).ToArray();
        var runs = new[] { Run("s", releases[6], Stage, 0) };

        var projected = Project(releases, runs, depth: 3);

        Assert.Equal(3, projected.ReleaseDepth);
        Assert.Equal(["CARD-7", "CARD-6", "CARD-5"], Keys(Column(projected, "stage").Landed));
        Assert.Empty(Column(projected, "production").Landed);
        Assert.Equal(DeliveryColumnProjection.MaxReleaseDepth, Project(releases, runs, depth: 500).ReleaseDepth);
        Assert.Equal(1, Project(releases, runs, depth: 0).ReleaseDepth);
    }

    [Fact]
    public void EmptyProject_HasOnlyTheIntegratedColumn()
    {
        var projected = DeliveryColumnProjection.Project("empty", [], [], [], new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<ProjectLifecyclePhase>>(), ["ONLY-1"]);

        var column = Assert.Single(projected.Columns);
        Assert.Equal(DeliveryColumnProjection.IntegratedColumnId, column.Id);
        Assert.Equal(["ONLY-1"], Keys(column.Landed));
    }
}
