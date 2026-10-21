using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RunnerModelAcceptanceProbeTests
{
    [Fact]
    public async Task Rejection_is_cached_for_one_credential_generation_and_refreshed_after_login()
    {
        var generation = "login-a";
        var calls = 0;
        var now = DateTimeOffset.UnixEpoch.AddHours(1);
        var probe = new RunnerModelAcceptanceProbe();
        probe.Configure((_, _, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? new ProcessResult(1, "", "HTTP 400 {\"error\":{\"message\":\"The gpt-6-sol model is not supported when using Codex with a ChatGPT account\",\"type\":\"invalid_request_error\"}}")
                : new ProcessResult(0, "OK", ""));
        }, _ => generation, () => now);

        await probe.RefreshAsync([("codex", "gpt-6-sol")], CancellationToken.None);
        Assert.Equal("unavailable", Assert.Single(probe.Snapshot("codex", ["gpt-6-sol"])).Status);
        await probe.RefreshAsync([("codex", "gpt-6-sol")], CancellationToken.None);
        Assert.Equal(1, calls);

        generation = "login-b";
        Assert.Equal("unknown", Assert.Single(probe.Snapshot("codex", ["gpt-6-sol"])).Status);
        await probe.RefreshAsync([("codex", "gpt-6-sol")], CancellationToken.None);
        Assert.Equal("ready", Assert.Single(probe.Snapshot("codex", ["gpt-6-sol"])).Status);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Model_rejection_parser_requires_the_requested_model()
    {
        var result = new ProcessResult(1, "", "HTTP 400 {\"error\":{\"message\":\"The gpt-6-sol model is not supported when using Codex with a ChatGPT account\",\"type\":\"invalid_request_error\"}}");
        Assert.True(RunnerModelAcceptanceProbe.IsModelRejection(result, "gpt-6-sol"));
        Assert.False(RunnerModelAcceptanceProbe.IsModelRejection(result, "gpt-6-luna"));
    }

    [Fact]
    public async Task Probe_started_after_a_launch_rejection_carries_a_larger_durable_sequence()
    {
        using var state = new TempStateDirectory();
        var now = DateTimeOffset.UnixEpoch.AddHours(1);
        var sequencePath = Path.Combine(state.Path, "model-acceptance-probe.sequence");
        var probe = new RunnerModelAcceptanceProbe();
        probe.Configure((_, _, _) => Task.FromResult(new ProcessResult(0, "OK", "")),
            _ => "login-a", () => now, sequencePath);

        await probe.RefreshAsync([("codex", "gpt-6-sol")], CancellationToken.None);
        var before = Sequence(probe);
        var rejection = probe.RecordRejection("codex", "gpt-6-sol");
        Assert.True(rejection > before);
        Assert.Equal("unavailable", Assert.Single(probe.Snapshot("codex", ["gpt-6-sol"])).Status);

        // A later daemon process continues the persisted sequence, so its
        // first probe is ordered after the earlier process's rejection.
        var restarted = new RunnerModelAcceptanceProbe();
        restarted.Configure((_, _, _) => Task.FromResult(new ProcessResult(0, "OK", "")),
            _ => "login-a", () => now, sequencePath);
        await restarted.RefreshAsync([("codex", "gpt-6-sol")], CancellationToken.None);
        Assert.Equal("ready", Assert.Single(restarted.Snapshot("codex", ["gpt-6-sol"])).Status);
        Assert.True(CapabilityProtocol.ModelProbeSupersedesRejection(Sequence(restarted), rejection));
        Assert.False(CapabilityProtocol.ModelProbeSupersedesRejection(before, rejection));
    }

    [Fact]
    public async Task Operator_override_outside_the_curated_list_is_probed_when_requested()
    {
        var options = RunnerOptions.Parse(
            ["--server", "http://127.0.0.1:5030", "--runner-id", "agent-runner-test",
                "--codex-cli", "/nonexistent/codex-for-model-probe-test"]).Item1;
        const string overrideModel = "gpt-6.2-nova";
        Assert.DoesNotContain(("codex", overrideModel), RunnerCapabilityProbe.ModelCandidates(options, []));

        var probed = new List<string>();
        var probe = new RunnerModelAcceptanceProbe();
        probe.Configure((_, model, _) =>
        {
            probed.Add(model);
            return Task.FromResult(new ProcessResult(0, "OK", ""));
        }, _ => "login-a", () => DateTimeOffset.UnixEpoch.AddHours(1));

        Assert.True(probe.RequestProbe(CapabilityProtocol.ModelAcceptance("codex", overrideModel)));
        Assert.False(probe.RequestProbe("model-acceptance:codex:--dangerous flag"));
        Assert.False(probe.RequestProbe("model-acceptance:other-cli:gpt-6.2-nova"));
        var candidates = RunnerCapabilityProbe.ModelCandidates(options, probe.RequestedModels());
        Assert.Contains(("codex", overrideModel), candidates);

        await probe.RefreshAsync(candidates.Where(item => item.Model == overrideModel), CancellationToken.None);
        Assert.Equal(new[] { overrideModel }, probed);
        var advertised = Assert.Single(probe.Snapshot("codex", [overrideModel]));
        Assert.Equal(CapabilityProtocol.ModelAcceptance("codex", overrideModel), advertised.Key);
        Assert.Equal("ready", advertised.Status);
    }

    private static long Sequence(RunnerModelAcceptanceProbe probe)
        => CapabilityProtocol.ModelProbeSequence(
            Assert.Single(probe.Snapshot("codex", ["gpt-6-sol"])).EvidenceId)!.Value;

    private sealed class TempStateDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"model-probe-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    [Fact]
    public async Task Inconclusive_probe_retries_after_bounded_backoff()
    {
        var now = DateTimeOffset.UnixEpoch.AddHours(1);
        var calls = 0;
        var probe = new RunnerModelAcceptanceProbe();
        probe.Configure((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new ProcessResult(1, "", "network unavailable"));
        }, _ => "login-a", () => now);

        await probe.RefreshAsync([("codex", "gpt-6-sol")], CancellationToken.None);
        await probe.RefreshAsync([("codex", "gpt-6-sol")], CancellationToken.None);
        Assert.Equal("unknown", Assert.Single(probe.Snapshot("codex", ["gpt-6-sol"])).Status);
        Assert.Equal(1, calls);

        now = now.AddMinutes(11);
        await probe.RefreshAsync([("codex", "gpt-6-sol")], CancellationToken.None);
        Assert.Equal(2, calls);
    }
}
