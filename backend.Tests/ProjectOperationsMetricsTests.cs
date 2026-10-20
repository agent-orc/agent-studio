using AgentStudio.Deploy;
using AgentStudio.Projects;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectOperationsMetricsTests
{
    private static readonly DateTimeOffset End = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DeployEnvironment Production = new("prod", "demo", "Production", 1, EnvironmentKinds.Production);
    private static readonly DeployEnvironment Stage = new("stage", "demo", "Staging", 0, EnvironmentKinds.Staging);

    [Fact]
    public void Calculates_production_frequency_lead_failure_fraction_and_recovery()
    {
        var releases = new[] { Release("one", "CARD-1"), Release("two", "CARD-2") };
        var runs = new[]
        {
            Run("stage", "one", DeploymentStates.Succeeded, -20),
            Run("prod", "one", DeploymentStates.Failed, -10),
            Run("prod", "one", DeploymentStates.Succeeded, -8),
            Run("prod", "two", DeploymentStates.Succeeded, -2),
            Run("prod", "two", DeploymentStates.Queued, -1),
        };
        var cards = new Dictionary<string, DateTime> { ["CARD-1"] = End.AddDays(-2).UtcDateTime,
            ["CARD-2"] = End.AddDays(-1).UtcDateTime };

        var result = DoraMetrics.Calculate([Stage, Production], releases, runs, cards, End.AddDays(-28), End);

        Assert.Equal(2, result.SuccessfulProductionDeploys);
        Assert.Equal(0.5, result.DeploysPerWeek);
        Assert.Equal(2, result.CardsMeasured);
        Assert.Equal(1d / 3d, result.ChangeFailureRate!.Value, 3);
        Assert.Equal(1, result.RecoveriesMeasured);
        Assert.Equal(2, result.MedianRecoveryHours);
        Assert.Equal(31, result.MedianCardToProductionHours);
    }

    [Fact]
    public void Empty_and_unfinished_history_has_no_invented_rates()
    {
        var result = DoraMetrics.Calculate([Production], [], [Run("prod", "none", DeploymentStates.Running, -1)],
            new Dictionary<string, DateTime>(), End.AddDays(-30), End);
        Assert.Equal(0, result.SuccessfulProductionDeploys);
        Assert.Null(result.MedianCardToProductionHours);
        Assert.Null(result.ChangeFailureRate);
        Assert.Null(result.MedianRecoveryHours);
    }

    [Fact]
    public void Successful_change_followed_by_rollback_counts_as_failed_change()
    {
        var changed = Run("prod", "new", DeploymentStates.Succeeded, -4);
        var rollback = Run("prod", "old", DeploymentStates.Succeeded, -1) with
        {
            Trigger = DeploymentTriggers.AutomaticRollback,
        };
        var result = DoraMetrics.Calculate([Production], [Release("new", "CARD-1")], [changed, rollback],
            new Dictionary<string, DateTime>(), End.AddDays(-7), End);
        Assert.Equal(1, result.SuccessfulProductionDeploys);
        Assert.Equal(1, result.ChangeFailureRate);
    }

    private static Release Release(string id, string card) => new(id, "demo", id, id, [], "", "", [card],
        End.AddDays(-3).UtcDateTime, "default");

    private static Deployment Run(string environment, string release, string state, int hoursAgo) =>
        new($"{environment}-{release}-{hoursAgo}", release, environment, state,
            End.AddHours(hoursAgo - 1).UtcDateTime,
            state == DeploymentStates.Running || state == DeploymentStates.Queued ? null : End.AddHours(hoursAgo).UtcDateTime,
            null, [], DeploymentTriggers.Deploy, null, End.AddHours(hoursAgo - 2).UtcDateTime);
}
