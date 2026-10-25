using AgentStudio.Deploy;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReleaseChannelPolicyTests
{
    private static readonly ProjectDeploymentDefinition Definition = new([], [], [], [],
        ProjectDeploymentVocabulary.DefaultStandardLifecycle(), [])
    {
        Lifecycles = new Dictionary<string, IReadOnlyList<ProjectLifecyclePhase>>
        {
            ["hotfix"] = [new("staging", true, [], null), new("production", false, ["Owner"], null)],
        },
        Channels =
        [
            new("default", "standard", null, null, true),
            new("preview", "standard", "rc", null, false),
            new("hotfix", "hotfix", null, "refs/heads/hotfix/*", false),
        ],
    };

    [Fact]
    public void Rules_choose_a_single_matching_channel_then_default()
    {
        Assert.Equal("preview", ReleaseChannelPolicy.Select(Definition, "1.2.3-rc.2", null, null, false).Channel?.Name);
        Assert.Equal("hotfix", ReleaseChannelPolicy.Select(Definition, null, "refs/heads/hotfix/urgent", null, false).Channel?.Name);
        Assert.Equal("default", ReleaseChannelPolicy.Select(Definition, "1.2.3", "refs/heads/main", null, false).Channel?.Name);
        Assert.Contains("branchPattern=refs/heads/hotfix/*",
            ReleaseChannelPolicy.Select(Definition, null, "refs/heads/main", "hotfix", false).Error);
        Assert.Equal("hotfix", ReleaseChannelPolicy.Select(Definition, null, "refs/heads/main", "hotfix", true).Channel?.Name);
        Assert.Contains("More than one channel rule matched",
            ReleaseChannelPolicy.Select(Definition, "1.2.3-rc.2", "refs/heads/hotfix/urgent", null, false).Error);
    }

    [Fact]
    public void Each_channel_resolves_its_lifecycle()
    {
        Assert.Null(ReleaseChannelPolicy.Lifecycle(Definition, Definition.Channels[2])[1].Requires);
        Assert.Equal("staging", ReleaseChannelPolicy.Lifecycle(Definition, Definition.Channels[0])[1].Requires);
        var environment = new DeployEnvironment("production", "demo", "Production", 1, "production");
        var actor = new DeployActor("owner", true);
        var facts = new LifecycleRequest(DeployIntent.Deploy, environment, actor, true, false,
            ReleasePosition.Newer, false, false, false);
        Assert.Equal(LifecycleOutcome.Reject, StandardLifecyclePolicy.Decide(facts
            with { Phases = Definition.StandardLifecycle }).Outcome);
        Assert.Equal(LifecycleOutcome.Queue, StandardLifecyclePolicy.Decide(facts
            with { Phases = Definition.Lifecycles["hotfix"] }).Outcome);
        Assert.Equal(LifecycleOutcome.Reject, StandardLifecyclePolicy.Decide(facts
            with { Actor = new DeployActor("maintainer", true, "Maintainer"),
                Phases = Definition.Lifecycles["hotfix"] }).Outcome);
    }

    [Fact]
    public void Variable_scopes_use_channel_and_environment_specificity()
    {
        ProjectVariableDefinition Value(string value, string[] environments, string[] channels)
            => new("LOG_LEVEL", value, null, environments) { Channels = channels };
        var variables = new[]
        {
            Value("base", [], []), Value("production", ["production"], []),
            Value("hotfix", [], ["hotfix"]), Value("hotfix-production", ["production"], ["hotfix"]),
        };
        Assert.Equal("hotfix-production", ProjectVariableScopePolicy.Resolve(variables, "LOG_LEVEL", "production", "hotfix")?.Value);
        Assert.Equal("production", ProjectVariableScopePolicy.Resolve(variables, "LOG_LEVEL", "production", "default")?.Value);
        Assert.Equal("hotfix", ProjectVariableScopePolicy.Resolve(variables, "LOG_LEVEL", "staging", "hotfix")?.Value);
        Assert.Equal("base", ProjectVariableScopePolicy.Resolve(variables, "LOG_LEVEL", "staging", "default")?.Value);
    }
}
