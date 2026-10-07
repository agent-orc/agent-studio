using AgentStudio.Runner;
using AgentStudio.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class SteeringFeedbackProjectionTests
{
    [Theory]
    [InlineData(true, "consumed")]
    [InlineData(false, "rejected")]
    public void Settled_stop_receipt_does_not_claim_run_recovery(bool observed, string terminalState)
    {
        // clock-independent: the fixed instant is injected into both receipt stores.
        var now = new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);
        var stops = new RemoteRunStopRequestStore(() => now);
        stops.Record("AGT-2938", "operator stop", "attempt-1", "operator", 4, "stop-1");
        if (observed)
        {
            now = now.AddMinutes(1);
            stops.Observe("AGT-2938", "attempt-1", 4);
        }
        now = now.AddMinutes(1);
        stops.Clear("AGT-2938", "attempt-1", 4);

        var authority = new AttemptAuthorityService(
            NullLogger<AttemptAuthorityService>.Instance, () => now);
        var projection = new SteeringFeedbackProjection(authority, stops);
        var task = new TaskInfo { TaskKey = "AGT-2938", State = "3-in-progress" };

        var feedback = projection.Build(task);
        var terminal = Assert.Single(feedback.History,
            fact => fact.Identity == "stop:stop-1:terminal");
        Assert.Equal(terminalState, terminal.State);
        Assert.Equal("stop-1", terminal.CommandId);
        Assert.Equal("attempt-1", terminal.AttemptId);
        Assert.DoesNotContain(feedback.History, fact => fact.State == "recovered");

        var timeline = SteeringFeedbackProjection.Timeline(feedback);
        var terminalEvent = Assert.Single(timeline,
            entry => entry.Details?.GetValueOrDefault("identity") == terminal.Identity);
        Assert.Equal(terminalState, terminalEvent.Details!["state"]);
    }
}
