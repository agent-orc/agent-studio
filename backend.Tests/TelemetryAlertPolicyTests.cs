using AgentStudio.Projects;
using System.Text.Json;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TelemetryAlertPolicyTests
{
    [Theory]
    [InlineData("5xx-rate", 0.8, true)]
    [InlineData("latency-p95", 4.0, true)]
    [InlineData("storage-usage", 0.96, true)]
    [InlineData("restarts", 1.0, true)]
    [InlineData("5xx-rate", 0.01, false)]
    [InlineData("latency-p95", 0.1, false)]
    [InlineData("storage-usage", 0.2, false)]
    [InlineData("restarts", 0.0, false)]
    public void Default_rules_fire_at_their_threshold(string rule, double value, bool firing)
    {
        var definition = Assert.Single(TelemetryAlertDeclarations.Defaults.Where(item => item.Id == rule));
        Assert.Equal(firing, TelemetryAlertPolicy.Firing(definition, value));
    }

    [Fact]
    public void Missing_observation_never_counts_as_recovery()
    {
        var current = new HealthCheckStreak(1, 0, true);
        Assert.Equal(current, TelemetryAlertPolicy.Next(current, null, 2).Streak);
    }

    [Fact]
    public void Declaration_overrides_threshold_window_and_reservation()
    {
        using var json = JsonDocument.Parse("""
            { "telemetryAlerts": { "storageReservationBytes": 10000000000,
              "5xxRate": { "threshold": 0.2, "windowMinutes": 4, "recoveryEvaluations": 3 } } }
            """);
        var policy = TelemetryAlertDeclarations.Parse(json.RootElement, "production.json");
        Assert.Equal(10_000_000_000, policy.StorageReservationBytes);
        Assert.Equal(new TelemetryAlertDefinition("5xx-rate", 0.2, 4, 3), policy.Rules[0]);
        Assert.Equal(TelemetryAlertDeclarations.Defaults.Skip(1), policy.Rules.Skip(1));
    }

    [Theory]
    [InlineData("{ \"5xxRate\": { \"threshold\": 1.1 } }")]
    [InlineData("{ \"restarts\": { \"windowMinutes\": 0 } }")]
    [InlineData("{ \"storageReservationBytes\": -1 }")]
    public void Invalid_alert_declarations_fail_the_environment(string alerts)
    {
        using var json = JsonDocument.Parse("{\"telemetryAlerts\":" + alerts + "}");
        Assert.Throws<JsonException>(() => TelemetryAlertDeclarations.Parse(json.RootElement, "production.json"));
    }
}
