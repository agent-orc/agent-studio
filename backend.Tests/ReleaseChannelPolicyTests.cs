using AgentStudio.Deploy;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReleaseChannelPolicyTests
{
    private static ProjectDeploymentDefinition Definition => new([], [], [], [], [], [])
    {
        Channels =
        [
            new("default", "standard", null, null, true),
            new("hotfix", "hotfix", "hotfix", "release/*", false),
        ],
    };

    [Theory]
    [InlineData("hotfix", "release/2.1", "hotfix")]
    [InlineData("hotfix", "main", "default")]
    [InlineData(null, "release/2.1", "default")]
    [InlineData(null, null, "default")]
    public void Rules_match_conjunctively_then_use_default(string? tag, string? branch, string expected)
    {
        var choice = ReleaseChannelPolicy.Choose(Definition, new(tag, branch, null), false);
        Assert.Null(choice.Error);
        Assert.Equal(expected, choice.Channel!.Name);
    }

    [Fact]
    public void Override_requires_approver_and_refusal_names_the_rule()
    {
        var facts = new ReleaseChannelFacts(null, "main", "hotfix");
        var refused = ReleaseChannelPolicy.Choose(Definition, facts, false);
        Assert.Contains("preRelease 'hotfix'", refused.Error);
        Assert.Contains("branch pattern 'release/*'", refused.Error);
        Assert.Equal("hotfix", ReleaseChannelPolicy.Choose(Definition, facts, true).Channel!.Name);
    }

    [Fact]
    public void Ambiguous_matching_rules_are_refused_with_both_rule_names()
    {
        var definition = Definition with
        {
            Channels = [.. Definition.Channels,
                new ProjectChannelDefinition("emergency", "standard", "hotfix", null, false)],
        };
        var choice = ReleaseChannelPolicy.Choose(definition, new("hotfix", "release/2.1", null), false);
        Assert.Contains("hotfix (", choice.Error);
        Assert.Contains("emergency (", choice.Error);
    }
}
