using AgentStudio.Cli;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: circuit time is injected and advanced explicitly.
public sealed class ProviderRoutingHealthTests
{
    [Fact]
    public void Error_rate_holds_only_the_failing_provider_and_expires_for_a_recovery_attempt()
    {
        var clock = new Clock();
        var health = new ProviderRoutingHealth(clock);
        for (var i = 0; i < 4; i++) health.Observe("claude", true);
        Assert.Null(health.UnavailableReason("claude"));
        health.Observe("claude", true);
        Assert.Contains("error rate", health.UnavailableReason("claude"));
        Assert.Null(health.UnavailableReason("codex"));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(health.UnavailableReason("claude"));
        health.Observe("claude", false);
        Assert.Null(health.UnavailableReason("claude"));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
