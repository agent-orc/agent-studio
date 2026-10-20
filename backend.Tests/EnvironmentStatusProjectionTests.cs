using AgentStudio.Deploy;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: pure projection and numbering inputs; the code under test never reads a clock.
public sealed class EnvironmentStatusProjectionTests
{
    private static readonly DateTime T0 = new(2026, 10, 16, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DeployEnvironment Staging = new("stage", "demo", "Stage", 0, EnvironmentKinds.Staging);
    private static readonly DeployEnvironment Production = new("production", "demo", "Production", 1, EnvironmentKinds.Production);
    private static readonly Release R1 = Release("r1", 1, ["WEB-1"]);
    private static readonly Release R2 = Release("r2", 2, ["WEB-2", "WEB-3"]);
    private static readonly Release R3 = Release("r3", 3, ["WEB-4"]);

    [Fact]
    public void Status_ShowsCurrentNewerReleaseAndCardsSinceLastDeploy()
    {
        var deployments = new[]
        {
            Run("d1", R1, Staging, DeploymentStates.Succeeded, 1),
            Run("d2", R1, Production, DeploymentStates.Succeeded, 2),
            Run("d3", R2, Staging, DeploymentStates.Succeeded, 3),
            Run("d4", R3, Staging, DeploymentStates.Running, 4),
            Run("d5", R2, Production, DeploymentStates.AwaitingApproval, 5),
        };
        var status = EnvironmentStatusProjection.Project([Production, Staging], [R1, R2, R3], deployments,
            new Dictionary<string, string> { ["stage"] = "healthy", ["production"] = "failed" });

        Assert.Equal(["stage", "production"], status.Select(item => item.Environment.Id));
        var staging = status[0];
        Assert.Equal(("r2", "d4", "r3"), (staging.CurrentRelease?.Id, staging.CurrentDeployment?.Id, staging.NewerRelease?.Id));
        Assert.Equal(["WEB-4"], staging.CardsSinceLastDeploy);
        Assert.Equal(EnvironmentHealth.Healthy, staging.Health);

        // Production only offers what Staging has proven: r2, not the newer r3.
        var production = status[1];
        Assert.Equal(("r1", "d5", "r2"), (production.CurrentRelease?.Id, production.CurrentDeployment?.Id, production.NewerRelease?.Id));
        Assert.Equal(["WEB-2", "WEB-3"], production.CardsSinceLastDeploy);
        Assert.Equal(EnvironmentHealth.Unhealthy, production.Health);
    }

    [Fact]
    public void EmptyEnvironments_ReportUnknownHealth_AndProductionHasNothingToPromote()
    {
        var status = EnvironmentStatusProjection.Project([Staging, Production], [R1, R2], [],
            new Dictionary<string, string> { ["stage"] = "unknown" });

        Assert.Null(status[0].CurrentRelease);
        Assert.Null(status[0].CurrentDeployment);
        Assert.Equal("r2", status[0].NewerRelease?.Id);
        Assert.Equal(["WEB-1", "WEB-2", "WEB-3"], status[0].CardsSinceLastDeploy);
        Assert.Null(status[1].NewerRelease);
        Assert.Empty(status[1].CardsSinceLastDeploy);
        Assert.All(status, item => Assert.Equal(EnvironmentHealth.Unknown, item.Health));
    }

    [Fact]
    public void Rollback_MakesTheOlderReleaseCurrent_AndOffersTheWithdrawnOneAgain()
    {
        var deployments = new[]
        {
            Run("d1", R1, Staging, DeploymentStates.Succeeded, 1),
            Run("d2", R2, Staging, DeploymentStates.Succeeded, 2),
            Run("d3", R2, Staging, DeploymentStates.Failed, 3),
            Run("d4", R1, Staging, DeploymentStates.Succeeded, 4) with { Trigger = DeploymentTriggers.Rollback },
        };
        var staging = EnvironmentStatusProjection.Project([Staging], [R1, R2], deployments,
            new Dictionary<string, string>())[0];
        Assert.Equal("r1", staging.CurrentRelease?.Id);
        Assert.Equal("d4", staging.CurrentDeployment?.Id);
        Assert.Equal("r2", staging.NewerRelease?.Id);
    }

    [Fact]
    public void PendingInterventionRemainsVisibleAfterANewerRunStarts()
    {
        var waiting = Run("older", R1, Staging, DeploymentStates.WaitingForIntervention, 1) with
        { FailedStep = DeployStepNames.Health, InterventionExpiresAtUtc = T0.AddHours(2) };
        var newer = Run("newer", R2, Staging, DeploymentStates.Running, 2);
        var status = EnvironmentStatusProjection.Project([Staging], [R1, R2], [waiting, newer],
            new Dictionary<string, string>())[0];

        Assert.Equal("newer", status.CurrentDeployment?.Id);
        Assert.Equal(("older", DeployStepNames.Health, T0.AddHours(2)),
            (status.Intervention?.DeploymentId, status.Intervention?.FailedStep, status.Intervention?.ExpiresAtUtc));
    }

    [Fact]
    public void PreviousRelease_IsTheLatestOlderReleaseThatSucceeded()
    {
        var releases = new[] { R1, R2, R3 }.ToDictionary(item => item.Id);
        var history = new[]
        {
            Run("d1", R1, Production, DeploymentStates.Succeeded, 1),
            Run("d2", R2, Production, DeploymentStates.Failed, 2),
            Run("d3", R3, Production, DeploymentStates.Succeeded, 3),
        };
        Assert.Equal("r1", EnvironmentStatusProjection.PreviousRelease(history, releases)?.Id);

        var afterRollback = history.Append(Run("d4", R1, Production, DeploymentStates.Succeeded, 4)).ToArray();
        Assert.Null(EnvironmentStatusProjection.PreviousRelease(afterRollback, releases));
        Assert.Null(EnvironmentStatusProjection.PreviousRelease([], releases));
    }

    [Fact]
    public void SkippedHealth_KeepsNewReleaseCurrentAndPreviousReleaseAvailableForRollback()
    {
        var releases = new[] { R1, R2 }.ToDictionary(item => item.Id);
        var previous = Run("d1", R1, Staging, DeploymentStates.Succeeded, 1);
        var skippedHealth = Run("d2", R2, Staging, DeploymentStates.Succeeded, 2) with
        { InterventionDecision = "skip", FailedStep = DeployStepNames.Health };
        var runs = new[] { previous, skippedHealth };

        var card = EnvironmentStatusProjection.Project([Staging], [R1, R2], runs,
            new Dictionary<string, string> { [Staging.Id] = "failed" })[0];
        Assert.Equal(R2.Id, card.CurrentRelease?.Id);
        Assert.Equal(EnvironmentHealth.Unhealthy, card.Health);
        Assert.Equal(skippedHealth.Id, card.Intervention?.DeploymentId);
        Assert.Equal(R1.Id, EnvironmentStatusProjection.PreviousRelease(runs, releases)?.Id);
        Assert.True(EnvironmentStatusProjection.SucceededIn(runs, R2.Id, Staging.Id));

        var skippedDeploy = Run("d3", R2, Staging, DeploymentStates.Succeeded, 3) with
        { InterventionDecision = "skip", FailedStep = DeployStepNames.StartContainer };
        Assert.False(EnvironmentStatusProjection.SucceededIn([skippedDeploy], R2.Id, Staging.Id));
        Assert.Null(EnvironmentStatusProjection.CurrentRelease([skippedDeploy], releases));
    }

    [Fact]
    public void ReleaseNumbering_CountsPerUtcDay_AndIdentityIsContentAddressed()
    {
        Assert.Equal("2026.10.16.4", ReleaseSnapshot.NextVersion([R1, R2, R3], T0));
        Assert.Equal("2026.10.17.1", ReleaseSnapshot.NextVersion([R1, R2, R3], T0.AddDays(1)));
        var digest = "registry.example/app@sha256:" + new string('a', 64);
        Assert.Equal(ReleaseSnapshot.Id("Demo", new string('1', 40), [digest]),
            ReleaseSnapshot.Id("demo", new string('1', 40), [digest]));
        Assert.NotEqual(ReleaseSnapshot.Id("demo", new string('1', 40), [digest]),
            ReleaseSnapshot.Id("demo", new string('2', 40), [digest]));
    }

    private static Release Release(string id, int number, string[] cards)
        => new(id, "demo", $"2026.10.16.{number}", new string((char)('0' + number), 40),
            ["registry.example/app@sha256:" + new string((char)('a' + number), 64)], "process", "variables",
            cards, T0.AddHours(number), ReleaseSnapshot.DefaultChannel);

    private static Deployment Run(string id, Release release, DeployEnvironment environment, string state, int hour)
        => new(id, release.Id, environment.Id, state, T0.AddHours(hour), DeploymentStates.IsFinished(state)
                ? T0.AddHours(hour).AddMinutes(5) : null, null, [], DeploymentTriggers.Deploy, "release-gate",
            T0.AddHours(hour));
}
