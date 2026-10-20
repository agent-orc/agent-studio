using AgentStudio.Deploy;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectChannelLifecycleTests
{
    [Fact]
    public void Channel_lifecycle_restricts_phases_and_names_the_failed_prerequisite()
    {
        var stage = new DeployEnvironment("stage", "demo", "Stage", 0, "staging");
        var production = new DeployEnvironment("production", "demo", "Production", 1, "production");
        var phases = new ProjectLifecyclePhase[]
        {
            new("staging", true, [], null),
            new("production", false, ["Owner"], "staging"),
        };
        LifecycleRequest Request(DeployEnvironment environment, bool succeeded, IReadOnlyList<ProjectLifecyclePhase> lifecycle)
            => new(DeployIntent.Deploy, environment, new DeployActor("owner", true), false, succeeded,
                ReleasePosition.Newer, false, false, false) { Channel = "hotfix", Phases = lifecycle };

        var missing = StandardLifecyclePolicy.Decide(Request(production, false, phases));
        Assert.Equal(LifecycleOutcome.Reject, missing.Outcome);
        Assert.Contains("Channel 'hotfix' lifecycle rule 'staging'", missing.Reason);
        Assert.Equal(LifecycleOutcome.AwaitApproval,
            StandardLifecyclePolicy.Decide(Request(production, true, phases)).Outcome);
        Assert.Contains("has no phase", StandardLifecyclePolicy.Decide(Request(stage, true,
            [new("production", false, ["Owner"], null)])).Reason);
    }

    [Fact]
    public void Channel_rules_choose_a_single_match_or_default_and_refuse_overlaps()
    {
        var channels = new ProjectChannelDefinition[]
        {
            new("hotfix", "standard", "hotfix", "release/*", false),
            new("preview", "standard", "preview", null, false),
            new("default", "standard", null, null, true),
        };
        var definition = new ProjectDeploymentDefinition([], [], [], [], [], []) { Channels = channels };
        Assert.Equal("default", ProjectChannelPolicy.Select(null, "1.0", null, null, false).Channel?.Name);
        Assert.Equal("hotfix", ProjectChannelPolicy.Select(definition, "1.0-hotfix", "release/one", null, false).Channel?.Name);
        Assert.Equal("default", ProjectChannelPolicy.Select(definition, "1.0-hotfixing", "release/one", null, false).Channel?.Name);
        Assert.Equal("default", ProjectChannelPolicy.Select(definition, "1.0-hotfix", "develop", null, false).Channel?.Name);
        Assert.Equal("preview", ProjectChannelPolicy.Select(definition, "1.0-preview", null, null, false).Channel?.Name);
        Assert.Equal("hotfix", ProjectChannelPolicy.Select(definition, "1.0", null, "hotfix", true).Channel?.Name);
        Assert.Contains("overlap", ProjectChannelPolicy.Select(definition with
        {
            Channels = [.. channels, new("also-preview", "standard", "preview", null, false)],
        }, "1.0-preview", null, null, false).Refusal);
    }
}
