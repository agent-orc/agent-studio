using AgentStudio.Pipeline;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

public sealed class GateEnvironmentContinuationPolicyTests
{
    [Theory]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 2, false)]
    [InlineData(false, 0, false)]
    public void HostFaultNeverStartsAProductRound(
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
        Assert.False(GateEnvironmentContinuationPolicy.ShouldStart(true,
            GateEnvironmentContinuationRounds.CountPriorRounds(events,
                "cccccccccccccccccccccccccccccccccccccccc")));
    }

    [Fact]
    public void HostBudgetSurvivesReloadAndRejectsConcurrentSpendingAcrossDeliveries()
    {
        var path = Path.Combine(Path.GetTempPath(), "gate-host-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var first = GateEnvironmentHostRepairs.Read(path, "gate-a", "compiler");
            Assert.True(GateEnvironmentHostRepairs.TryWrite(path, first, first with { AttemptsSpent = 3, Quarantined = true }));
            Assert.False(GateEnvironmentHostRepairs.TryWrite(path, first, first with { AttemptsSpent = 1 }));
            var reloaded = GateEnvironmentHostRepairs.Read(path, "GATE-A", "compiler");
            Assert.Equal(3, reloaded.AttemptsSpent);
            Assert.True(reloaded.Quarantined);
            Assert.Equal(0, GateEnvironmentHostRepairs.Read(path, "gate-b", "compiler").AttemptsSpent);
            Assert.Equal(0, GateEnvironmentHostRepairs.Read(path, "gate-a", "runtime").AttemptsSpent);
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public void CorruptHostBudgetFailsClosedInsteadOfRestoringRetries()
    {
        var path = Path.Combine(Path.GetTempPath(), "gate-host-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            File.WriteAllText(Path.Combine(path, GateEnvironmentHostRepairs.FileName), "{");
            Assert.Throws<System.Text.Json.JsonException>(() => GateEnvironmentHostRepairs.Read(path, "gate-a", "compiler"));
        }
        finally { Directory.Delete(path, recursive: true); }
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
