using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PendingCodePushHealthPolicyTests
{
    [Fact]
    public void Alert_appears_only_after_the_pending_push_age_limit()
    {
        var now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        var recent = new BatchIntegrationReservationService.PendingCodePush(
            "recent", "sha-1", new DateTimeOffset(now).AddMinutes(-19));
        var stale = new BatchIntegrationReservationService.PendingCodePush(
            "stale", "sha-2", new DateTimeOffset(now).AddMinutes(-20));

        var alert = Assert.Single(PendingCodePushHealthPolicy.Alerts([recent, stale], now));
        Assert.Equal(PipelineHealthConventions.PendingCodePushStalledKind, alert.Kind);
        Assert.Equal("stale", alert.JobId);
        Assert.Contains("stale@sha-2", alert.Detail);
    }
}
