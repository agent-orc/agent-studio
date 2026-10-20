using AgentStudio.OrchestrationDecisions;
using AgentStudio.Registry;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PolicySignalProducersTests
{
    private static readonly DateTime At = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly GroundsSourceRefV1 Source = new("receipt", "receipt:sample");

    [Fact]
    public void Emergency_limit_uses_project_then_workspace_then_24_hour_platform_default()
    {
        Assert.Equal((86_400, "platform"), CodingEmergencyLimit.Resolve(null, null));
        Assert.Equal((36_000, "workspace"), CodingEmergencyLimit.Resolve(null, 36_000));
        Assert.Equal((43_200, "project"), CodingEmergencyLimit.Resolve(43_200, 36_000));
        Assert.Equal((36_000, "workspace"), CodingEmergencyLimit.Resolve(0, 36_000));
        Assert.Equal((43_200, "project"), OrchestratorSettingsResolver.ResolveCodingEmergencyLimit(
            new() { CodingEmergencyLimitSeconds = 43_200 },
            new() { CodingEmergencyLimitSeconds = 36_000 }));
    }

    [Fact]
    public void Eight_policy_producers_emit_observations_without_action_fields()
    {
        var aspect = ReviewAspectBudgetPolicy.Derive("codex", "gpt-6-sol", "medium", 2400);
        var signals = new[]
        {
            PolicySignalProducers.GateDuration(150, "p95=100s × 1.5; cohort=project; override=none", 20, At, Source),
            PolicySignalProducers.ReviewAspect(aspect, At, Source),
            PolicySignalProducers.WatcherContingent(800, 3200, 2, 9, false, At, Source),
            PolicySignalProducers.CodingRun(700, 86_400, "platform", false, At, Source),
            PolicySignalProducers.Salvage(1, 1, true, At, Source),
            PolicySignalProducers.ReviewReissue(2, 2, At, Source),
            PolicySignalProducers.GitCalls(12, 30, TimeSpan.FromSeconds(8), At, Source),
            PolicySignalProducers.GateEnvironmentRetry("environment:missing-sdk", 2, 2, At, Source),
        };

        Assert.Equal(8, signals.Select(signal => signal.Id).Distinct().Count());
        Assert.All(signals, signal =>
        {
            Assert.Equal("signal", signal.Block);
            Assert.Equal(Source, signal.Source);
            Assert.False(signal.Required);
        });
        Assert.Contains("p95=100s × 1.5", signals[0].Value);
        Assert.Contains(aspect.Derivation, signals[1].Value);
        Assert.Contains("exhausted=True", signals[4].Value);
    }
}
