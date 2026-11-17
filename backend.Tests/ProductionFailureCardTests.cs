using AgentStudio.Deploy;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: pure card wording; the code under test never reads a clock.
public sealed class ProductionFailureCardTests
{
    private static readonly DateTime T0 = new(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Release R1 = Release("r1", 1, ["SHOP-4"]);
    private static readonly Release R2 = Release("r2", 2, ["SHOP-5"]);
    private static readonly Release R3 = Release("r3", 3, ["SHOP-6"]);

    [Fact]
    public void Deploy_ListsTheCardsOfEveryReleaseSinceTheLastHealthyOne_AndNamesIt()
    {
        // F15: release 2 never reached Production, so release 3 took SHOP-5 live too.
        var failed = Run("d3", R3, DeploymentStates.Failed, 3);
        var runs = new[]
        {
            Run("d1", R1, DeploymentStates.Succeeded, 1),
            Run("d2", R2, DeploymentStates.Cancelled, 2),
            failed,
        };

        var card = ProductionFailureCard.Build("demo", failed, runs, [R1, R2, R3], "- health: failed");

        Assert.Equal("Investigate Production deployment of release 2026.11.16.3", card.Title);
        Assert.Contains("Last healthy release in Production: 2026.11.16.1 (`r1`)", card.Prompt);
        Assert.Contains("Cards added since 2026.11.16.1: SHOP-5, SHOP-6", card.Prompt);
        Assert.DoesNotContain("SHOP-4", card.Prompt);
        Assert.Contains("- health: failed", card.Prompt);
    }

    [Fact]
    public void Deploy_IgnoresLaterRunsAndOtherTenants_WhenFindingTheHealthyRelease()
    {
        var failed = Run("d2", R2, DeploymentStates.Failed, 2);
        var runs = new[]
        {
            Run("d1", R1, DeploymentStates.Succeeded, 1),
            Run("t1", R2, DeploymentStates.Succeeded, 1) with { Tenant = "acme" },
            failed,
            Run("d3", R3, DeploymentStates.Succeeded, 4),
        };

        var card = ProductionFailureCard.Build("demo", failed, runs, [R1, R2, R3], "");

        Assert.Contains("Last healthy release in Production: 2026.11.16.1", card.Prompt);
        Assert.Contains("Cards added since 2026.11.16.1: SHOP-5\n", card.Prompt);
    }

    [Fact]
    public void Deploy_WithoutAHealthyRelease_ListsEveryReleaseUpToTheFailedOne()
    {
        var failed = Run("d2", R2, DeploymentStates.Failed, 2);

        var card = ProductionFailureCard.Build("demo", failed, [failed], [R1, R2, R3], "");

        Assert.Contains("Last healthy release in Production: none", card.Prompt);
        Assert.Contains("Cards in every release up to 2026.11.16.2: SHOP-4, SHOP-5\n", card.Prompt);
    }

    [Theory]
    [InlineData(DeploymentTriggers.AutomaticRollback, "Requested: automatically after a failed deployment")]
    [InlineData(DeploymentTriggers.Rollback, "Requested: by owner")]
    public void FailedRollback_IsTitledAsARollback_AndPointsAtTheFailureItFollowed(string trigger, string requested)
    {
        var rollback = Run("rb", R1, DeploymentStates.Failed, 4) with { Trigger = trigger, RequestedBy = "owner" };
        var runs = new[]
        {
            Run("d1", R1, DeploymentStates.Succeeded, 1),
            Run("d3", R3, DeploymentStates.Failed, 3),
            rollback,
        };

        var card = ProductionFailureCard.Build("demo", rollback, runs, [R1, R2, R3], "");

        Assert.Equal("Rollback to release 2026.11.16.1 failed in Production", card.Title);
        Assert.DoesNotContain("Investigate Production deployment", card.Title);
        Assert.Contains("# Production rollback failed", card.Prompt);
        Assert.Contains(requested, card.Prompt);
        Assert.Contains("Rolled back from: release 2026.11.16.3, deployment `d3` (failed)", card.Prompt);
        Assert.Contains("Failure card: `production-deploy-failed-d3`", card.Prompt);
    }

    [Fact]
    public void SecondFailedRollback_StillPointsAtTheOriginalDeployFailure()
    {
        var second = Run("rb2", R1, DeploymentStates.Failed, 5) with { Trigger = DeploymentTriggers.Rollback };
        var runs = new[]
        {
            Run("d1", R1, DeploymentStates.Succeeded, 1),
            Run("d3", R3, DeploymentStates.Failed, 3),
            Run("rb1", R1, DeploymentStates.Failed, 4) with { Trigger = DeploymentTriggers.AutomaticRollback },
            second,
        };

        var card = ProductionFailureCard.Build("demo", second, runs, [R1, R2, R3], "");

        Assert.Contains("Failure card: `production-deploy-failed-d3`", card.Prompt);
    }

    private static Release Release(string id, int number, IReadOnlyList<string> cards)
        => new(id, "demo", $"2026.11.16.{number}", "c" + number, ["registry/app@sha256:" + id], "p", "v", cards,
            T0.AddHours(number), ReleaseSnapshot.DefaultChannel);

    private static DeploymentRecord Run(string id, Release release, string state, int hour)
        => new()
        {
            Id = id, ReleaseId = release.Id, EnvironmentId = "production", State = state,
            Trigger = DeploymentTriggers.Deploy, DeployStep = DeployStepNames.StartContainer,
            CreatedAtUtc = T0.AddHours(hour).AddMinutes(1), StartedAtUtc = T0.AddHours(hour).AddMinutes(2),
            FinishedAtUtc = T0.AddHours(hour).AddMinutes(3),
        };
}
