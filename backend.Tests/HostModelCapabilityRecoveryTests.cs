using AgentStudio.Cli;
using AgentStudio.Runner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

public sealed class HostModelCapabilityRecoveryTests
{
    [Fact]
    public void Delayed_advertisement_with_runner_clock_ahead_cannot_clear_a_model_rejection()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var registry = new V1ReviewExecutorRegistry(clock);
        registry.Register("runner-a", new Contract.RegisterRunnerRequest(
            "runner-a", "host-a", "instance-a", "test", Contract.TaskServerProtocol.Current,
            [Contract.ReviewCapabilities.CodingExecutor]));
        var key = Contract.CapabilityProtocol.ModelAcceptance("codex", "gpt-6-sol");
        long generation = 0;

        Advertise("ready", 1);
        Assert.True(registry.AcceptsModel("runner-a", "instance-a", "codex", "gpt-6-sol"));

        clock.Advance(TimeSpan.FromSeconds(1));
        registry.MarkModelRejected("runner-a", "codex", "gpt-6-sol");

        // Built before the runner recorded the refusal; its runner clock reads
        // a minute later than the server's rejection time.
        clock.Advance(TimeSpan.FromSeconds(1));
        var delayed = Advertise("ready", 1);
        Assert.False(registry.AcceptsModel("runner-a", "instance-a", "codex", "gpt-6-sol"));
        Assert.Equal("unavailable", Assert.Single(delayed.Capabilities).AdvertisedStatus);

        // Without the runner's own refusal verdict no ready verdict is ordered after it.
        Assert.Equal("unavailable", Assert.Single(Advertise("ready", 2).Capabilities).AdvertisedStatus);
        Advertise("unavailable", 3);
        Assert.Equal("unavailable", Assert.Single(Advertise("ready", 3).Capabilities).AdvertisedStatus);
        Assert.False(registry.AcceptsModel("runner-a", "instance-a", "codex", "gpt-6-sol"));

        var recovered = Advertise("ready", 4);
        Assert.True(registry.AcceptsModel("runner-a", "instance-a", "codex", "gpt-6-sol"));
        Assert.Equal("ready", Assert.Single(recovered.Capabilities).AdvertisedStatus);

        Contract.RunnerCapabilitySnapshotDto Advertise(string status, long probeSequence)
        {
            var runnerNow = clock.GetUtcNow().UtcDateTime.AddMinutes(1);
            return registry.AdvertiseCapabilities("runner-a", new Contract.CapabilityAdvertisementRequest(
                "runner-a", "instance-a", Contract.CapabilityProtocol.CurrentSchemaVersion,
                runnerNow, 180, ++generation,
                [new Contract.AdvertisedCapabilityDto(key, "model-acceptance", status,
                    CredentialGeneration: "login-a", CredentialObservedAt: runnerNow,
                    EvidenceId: Contract.CapabilityProtocol.ModelProbeEvidence(probeSequence))]));
        }
    }

    [Fact]
    public void Capability_snapshot_names_configured_routes_including_operator_overrides()
    {
        var root = Path.Combine(Path.GetTempPath(), $"host-model-routes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var routes = new CliQuotaFallbackService(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = root })
                    .Build(),
                NullLogger<CliQuotaFallbackService>.Instance);
            routes.Set(new CliModelRouteProfile
            {
                CliType = "claude",
                PrimaryModel = "claude-opus-6",
                FallbackCliType = "codex",
                FallbackModel = "GPT-6.2-Sol-Preview",
            });
            routes.Set(new CliModelRouteProfile { CliType = "codex", FallbackModel = "not a model" });

            var candidates = V1ReviewPlaneEndpoints.ConfiguredModelRoutes(routes);

            Assert.Contains("claude/claude-opus-6", candidates);
            Assert.Contains("codex/gpt-6.2-sol-preview", candidates);
            Assert.DoesNotContain(candidates, item => item.Contains(' '));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("instance-a", null, "ready", 9L, "held", null)]
    [InlineData("instance-a", null, "ready", null, "held", null)]
    [InlineData("instance-a", null, "unavailable", 4L, "held", 4L)]
    [InlineData("instance-a", null, "unknown", 4L, "held", null)]
    [InlineData("instance-a", 4L, "unavailable", 2L, "held", 4L)]
    [InlineData("instance-a", 4L, "ready", 4L, "held", 4L)]
    [InlineData("instance-a", 4L, "ready", null, "held", 4L)]
    [InlineData("instance-a", 4L, "ready", 5L, "cleared", null)]
    [InlineData("instance-old", 4L, "ready", 7L, "rebound", 7L)]
    [InlineData("instance-old", 4L, "unknown", null, "rebound", 0L)]
    [InlineData(null, null, "ready", 3L, "rebound", 3L)]
    public void Recovery_policy_orders_verdicts_by_the_runner_probe_sequence(
        string? holdInstance, long? confirmed, string status, long? sequence, string expected, long? expectedSequence)
    {
        var next = Contract.ModelRejectionRecoveryPolicy.Observe(
            new Contract.ModelRejectionRecoveryPolicy.Hold(holdInstance, confirmed), "instance-a", status, sequence);

        if (expected == "cleared")
        {
            Assert.Null(next);
            return;
        }
        Assert.NotNull(next);
        Assert.Equal("instance-a", next.InstanceId);
        Assert.Equal(expectedSequence, next.ConfirmedSequence);
    }
}
