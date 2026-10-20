using AgentStudio.Deploy;
using Xunit;

namespace AgentStudio.Tests;

// Pure projections use the supplied UTC timestamps and never read a clock.
public sealed class CardDeploymentProjectionTests
{
    private static readonly DateTime At = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DeployEnvironment Stage = new("stage", "demo", "Staging", 0, EnvironmentKinds.Staging);
    private static readonly DeployEnvironment Production = new("production", "demo", "Production", 1, EnvironmentKinds.Production);
    private static readonly Release R1 = Release("r1", 1, ["WEB-1"]);
    private static readonly Release R2 = Release("r2", 2, ["WEB-2"]);

    [Fact]
    public void CardBadges_ShowReleaseTimeAndNotYetInProduction()
    {
        var views = CardDeploymentProjection.Project("demo", [Stage, Production], [R1, R2],
            [Run("d1", R1, Stage, DeploymentStates.Succeeded, 1),
             Run("d2", R1, Production, DeploymentStates.Succeeded, 2),
             Run("d3", R2, Stage, DeploymentStates.Succeeded, 3)], ["WEB-3"]);

        var newer = Assert.Single(views.Where(item => item.CardKey == "WEB-2"));
        Assert.Equal((DeploymentStates.Succeeded, "r2", "2026.10.16.2", At.AddHours(3)),
            (newer.Environments[0].State, newer.Environments[0].ReleaseId,
                newer.Environments[0].ReleaseVersion, newer.Environments[0].AtUtc));
        Assert.Equal(CardDeploymentProjection.NotDeployed, newer.Environments[1].State);
        Assert.All(Assert.Single(views.Where(item => item.CardKey == "WEB-3")).Environments,
            badge => Assert.Equal(CardDeploymentProjection.NotDeployed, badge.State));
    }

    [Fact]
    public void Rollback_MarksWithdrawnCardButKeepsOlderCardLive()
    {
        var views = CardDeploymentProjection.Project("demo", [Production], [R1, R2],
            [Run("d1", R1, Production, DeploymentStates.Succeeded, 1),
             Run("d2", R2, Production, DeploymentStates.Succeeded, 2),
             Run("d3", R1, Production, DeploymentStates.Succeeded, 3) with { Trigger = DeploymentTriggers.Rollback }]);

        Assert.Equal(DeploymentStates.Succeeded, Assert.Single(views.Where(item => item.CardKey == "WEB-1"))
            .Environments[0].State);
        var withdrawn = Assert.Single(views.Where(item => item.CardKey == "WEB-2")).Environments[0];
        Assert.Equal((CardDeploymentProjection.RolledBack, "r2", At.AddHours(3)),
            (withdrawn.State, withdrawn.ReleaseId, withdrawn.AtUtc));
    }

    [Fact]
    public void DivergentRelease_DoesNotInheritCardsFromAnEarlierRelease()
    {
        var views = CardDeploymentProjection.Project("demo", [Production], [R1, R2],
            [Run("d1", R1, Production, DeploymentStates.Succeeded, 1),
             Run("d2", R2, Production, DeploymentStates.Succeeded, 2)],
            releaseContains: (_, _) => false);

        Assert.Equal(CardDeploymentProjection.NotDeployed,
            Assert.Single(views.Where(item => item.CardKey == "WEB-1")).Environments[0].State);
        Assert.Equal(DeploymentStates.Succeeded,
            Assert.Single(views.Where(item => item.CardKey == "WEB-2")).Environments[0].State);
    }

    [Fact]
    public void DescendantRelease_KeepsCardsFromAnAncestorRelease()
    {
        var views = CardDeploymentProjection.Project("demo", [Production], [R1, R2],
            [Run("d1", R2, Production, DeploymentStates.Succeeded, 2)],
            releaseContains: (origin, target) => origin.Id == R1.Id && target.Id == R2.Id);

        var badge = Assert.Single(views.Where(item => item.CardKey == "WEB-1")).Environments[0];
        Assert.Equal((DeploymentStates.Succeeded, R2.Id), (badge.State, badge.ReleaseId));
    }

    [Theory]
    [InlineData(DeploymentStates.Queued)]
    [InlineData(DeploymentStates.Failed)]
    public void RollbackMark_SurvivesLaterUnsuccessfulAttempt(string attemptedState)
    {
        var views = CardDeploymentProjection.Project("demo", [Production], [R1, R2],
            [Run("d1", R2, Production, DeploymentStates.Succeeded, 1),
             Run("d2", R1, Production, DeploymentStates.Succeeded, 2) with { Trigger = DeploymentTriggers.Rollback },
             Run("d3", R2, Production, attemptedState, 3)]);

        var badge = Assert.Single(views.Where(item => item.CardKey == "WEB-2")).Environments[0];
        Assert.Equal((CardDeploymentProjection.RolledBack, R2.Id, At.AddHours(2)),
            (badge.State, badge.ReleaseId, badge.AtUtc));
    }

    [Fact]
    public void Notes_UseIncludedCardTitles_AndMissingCardsKeepTheirKeys()
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["WEB-2"] = "Faster page loading" };
        var notes = ReleaseNotesProjection.Project(R2 with { CardKeys = ["WEB-2", "WEB-3"] }, titles);
        Assert.True(notes.Preview);
        Assert.Equal([new ReleaseNote("WEB-2", "Faster page loading"), new ReleaseNote("WEB-3", "WEB-3")], notes.Notes);
        Assert.False(ReleaseNotesProjection.Project(R2, titles, preview: false).Preview);
    }

    [Theory]
    [InlineData("feat(WEB-2): improve checkout", "WEB-2", true)]
    [InlineData("Squash WEB-2 and WEB-3", "WEB-2", true)]
    [InlineData("Fix WEB-20", "WEB-2", false)]
    public void CommitMessageAttribution_SurvivesRebaseAndSquashWithoutPrefixCollisions(
        string message, string key, bool expected)
        => Assert.Equal(expected, ReleaseCardAttribution.ContainsKey(message, key));

    [Fact]
    public void RewrittenHistory_DoesNotReincludeACardFromAnEarlierRelease()
    {
        var revisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "new-sha" };
        var mentioned = ReleaseCardAttribution.FindKeys(["Squash WEB-1 and WEB-2"]);
        var prior = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WEB-1" };
        Assert.False(ReleaseCardAttribution.Includes("WEB-1", ["old-sha"], revisions, mentioned, prior));
        Assert.True(ReleaseCardAttribution.Includes("WEB-2", ["old-sha"], revisions, mentioned, prior));
        Assert.True(ReleaseCardAttribution.Includes("WEB-1", ["new-sha"], revisions, mentioned, prior));
    }

    private static Release Release(string id, int sequence, string[] cards)
        => new(id, "demo", $"2026.10.16.{sequence}", new string((char)('0' + sequence), 40),
            [], "process", "variables", cards, At.AddHours(sequence), "default");

    private static Deployment Run(string id, Release release, DeployEnvironment environment, string state, int hour)
        => new(id, release.Id, environment.Id, state, At.AddHours(hour), At.AddHours(hour), null, [],
            DeploymentTriggers.Deploy, "operator", At.AddHours(hour));
}
