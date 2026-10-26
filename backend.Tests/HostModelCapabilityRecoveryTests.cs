using AgentStudio.Cli;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
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
        var runnerAhead = TimeSpan.FromMinutes(1);

        Advertise(1, "ready", 4);
        Assert.True(registry.AcceptsModel("runner-a", "instance-a", "codex", "gpt-6-sol"));

        clock.Advance(TimeSpan.FromSeconds(1));
        registry.MarkModelRejected("runner-a", "codex", "gpt-6-sol");

        // Built before the rejection, delivered after it. The runner clock is
        // ahead, so its observation time looks newer than the rejection.
        clock.Advance(TimeSpan.FromSeconds(1));
        var delayed = Advertise(2, "ready", 4);
        Assert.False(registry.AcceptsModel("runner-a", "instance-a", "codex", "gpt-6-sol"));
        Assert.Equal("unavailable", Assert.Single(delayed.Capabilities).AdvertisedStatus);

        clock.Advance(TimeSpan.FromSeconds(1));
        Advertise(3, "unavailable", 5);
        clock.Advance(TimeSpan.FromSeconds(1));
        var replayed = Advertise(4, "ready", 5);
        Assert.Equal("unavailable", Assert.Single(replayed.Capabilities).AdvertisedStatus);

        clock.Advance(TimeSpan.FromSeconds(1));
        var recovered = Advertise(5, "ready", 6);
        Assert.True(registry.AcceptsModel("runner-a", "instance-a", "codex", "gpt-6-sol"));
        Assert.Equal("ready", Assert.Single(recovered.Capabilities).AdvertisedStatus);

        Contract.RunnerCapabilitySnapshotDto Advertise(long generation, string status, long sequence)
            => registry.AdvertiseCapabilities("runner-a", new Contract.CapabilityAdvertisementRequest(
                "runner-a", "instance-a", Contract.CapabilityProtocol.CurrentSchemaVersion,
                clock.GetUtcNow().UtcDateTime, 180, generation,
                [new Contract.AdvertisedCapabilityDto(
                    Contract.CapabilityProtocol.ModelAcceptance("codex", "gpt-6-sol"), "model-acceptance", status,
                    CredentialGeneration: "login-a",
                    CredentialObservedAt: clock.GetUtcNow().UtcDateTime + runnerAhead,
                    ProbeSequence: sequence)]));
    }

    [Theory]
    [InlineData(null, null, "instance-a", "ready", 9L, false, null, null)]
    [InlineData(null, null, "instance-a", "unavailable", 3L, false, "instance-a", 3L)]
    [InlineData(null, null, "instance-a", "unknown", null, false, null, null)]
    [InlineData("instance-a", 3L, "instance-a", "ready", 3L, false, "instance-a", 3L)]
    [InlineData("instance-a", 3L, "instance-a", "ready", 4L, true, "instance-a", 3L)]
    [InlineData("instance-a", 3L, "instance-b", "ready", 4L, false, "instance-a", 3L)]
    [InlineData("instance-a", 3L, "instance-b", "unknown", 1L, false, "instance-b", 1L)]
    [InlineData("instance-a", 3L, "instance-a", "ready", null, false, "instance-a", 3L)]
    public void Recovery_policy_requires_a_same_instance_sequence_after_a_non_ready_entry(
        string? ackInstance, long? ackSequence, string instance, string status, long? sequence,
        bool cleared, string? nextInstance, long? nextSequence)
    {
        var (actualCleared, next) = Contract.ModelAcceptanceRecovery.Evaluate(
            new Contract.ModelRejectionAcknowledgement(ackInstance, ackSequence), instance, status, sequence);

        Assert.Equal(cleared, actualCleared);
        Assert.Equal(new Contract.ModelRejectionAcknowledgement(nextInstance, nextSequence), next);
    }

    [Fact]
    public void Capability_response_names_operator_configured_primary_and_fallback_models()
    {
        var routes = V1ReviewPlaneEndpoints.ConfiguredModelRoutes(
        [
            new CliModelRouteProfile
            {
                CliType = "claude", PrimaryModel = "claude-opus-5-5",
                FallbackCliType = "codex", FallbackModel = "gpt-6.3-sol",
            },
            new CliModelRouteProfile { CliType = "codex" },
        ]);

        Assert.Equal(
            [
                new Contract.ConfiguredModelRouteDto("claude", "claude-opus-5-5"),
                new Contract.ConfiguredModelRouteDto("codex", "gpt-6.3-sol"),
            ],
            routes);
    }

    [Fact]
    public void Coding_routes_include_ready_card_pins_outside_the_builtin_catalogue()
    {
        var routes = V1ReviewPlaneEndpoints.CodingConfiguredModelRoutes(
            [],
            [new TaskInfo { State = TaskStates.Ready, CliType = "codex", Model = "gpt-6.3-sol" },
             new TaskInfo { State = TaskStates.Completed, CliType = "claude", Model = "claude-old" }]);

        Assert.Equal([new Contract.ConfiguredModelRouteDto("codex", "gpt-6.3-sol")], routes);
    }
}
