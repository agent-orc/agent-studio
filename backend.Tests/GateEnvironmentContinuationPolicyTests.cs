using AgentStudio.Pipeline;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

public sealed class GateEnvironmentContinuationPolicyTests
{
    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, false)]
    [InlineData(true, 2, false)]
    [InlineData(false, 0, false)]
    public void StartsOnlyOneAutomaticRoundForTheDelivery(
        bool automaticEnabled, int priorRounds, bool expected)
    {
        Assert.Equal(expected, GateEnvironmentContinuationPolicy.ShouldStart(
            automaticEnabled, priorRounds));
    }

    [Fact]
    public void PriorRounds_OnlyCountTheCurrentDelivery()
    {
        const string oldSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string currentSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var events = new[]
        {
            Queued(oldSha),
            Queued(currentSha),
            Queued(currentSha.ToUpperInvariant()),
            new TimelineEvent
            {
                Kind = TimelineEventKinds.IntegrationRecoveryQueued,
                Details = new Dictionary<string, string>
                {
                    ["source"] = "acceptance-rail",
                    ["deliverySha"] = currentSha,
                },
            },
        };

        Assert.Equal(1, GateEnvironmentContinuationRounds.CountPriorRounds(events, oldSha));
        Assert.Equal(2, GateEnvironmentContinuationRounds.CountPriorRounds(events, currentSha));
        Assert.Equal(0, GateEnvironmentContinuationRounds.CountPriorRounds(events,
            "cccccccccccccccccccccccccccccccccccccccc"));
        Assert.False(GateEnvironmentContinuationPolicy.ShouldStart(true,
            GateEnvironmentContinuationRounds.CountPriorRounds(events, currentSha)));
        Assert.True(GateEnvironmentContinuationPolicy.ShouldStart(true,
            GateEnvironmentContinuationRounds.CountPriorRounds(events,
                "cccccccccccccccccccccccccccccccccccccccc")));
    }

    private static TimelineEvent Queued(string deliverySha) => new()
    {
        Kind = TimelineEventKinds.IntegrationRecoveryQueued,
        Details = new Dictionary<string, string>
        {
            ["source"] = "gate-environment-continuation",
            ["deliverySha"] = deliverySha,
        },
    };
}
