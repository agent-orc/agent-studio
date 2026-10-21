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
    public void Configured_operator_override_outside_the_curated_list_is_probed()
    {
        var probe = new RunnerModelAcceptanceProbe();
        probe.Configure((_, _, _) => Task.FromResult(new ProcessResult(0, "OK", "")), _ => "login-a");
        var options = new RunnerOptions
        {
            ServerUrl = "http://task-server", RunnerId = "test", RunnerName = "test",
            Hostname = "test", BackendName = "test", WorkDir = Path.GetTempPath(),
            BaseBranch = "main", Role = "coding",
            CodexCliBin = Path.Combine(Path.GetTempPath(), "missing-codex-cli"),
            ClaudeCliBin = Path.Combine(Path.GetTempPath(), "missing-claude-cli"),
        };

        probe.ObserveServer(DateTime.UnixEpoch.AddDays(1),
        [
            CapabilityProtocol.ModelAcceptance("codex", "gpt-7-operator-pin"),
            CapabilityProtocol.ModelAcceptance("claude", "claude-operator-fallback"),
            "model-acceptance:codex:gpt-7 --dangerously-bypass",
            "model-acceptance:gemini:gemini-3",
        ], []);
        var candidates = RunnerCapabilityProbe.ModelCandidates(options, probe);

        Assert.Contains(("codex", "gpt-7-operator-pin"), candidates);
        Assert.Contains(("claude", "claude-operator-fallback"), candidates);
        Assert.Contains(("codex", "gpt-6-sol"), candidates);
        Assert.DoesNotContain(candidates, candidate => candidate.Model.Contains(' ', StringComparison.Ordinal));
        Assert.DoesNotContain(candidates, candidate => candidate.Provider == "gemini");
    }

    [Fact]
    public async Task Probe_carries_the_server_watermark_received_before_it_started()
    {
        var serverBefore = DateTime.UnixEpoch.AddDays(1);
        var probe = new RunnerModelAcceptanceProbe();
        probe.Configure((_, _, _) =>
        {
            // A response that lands while the request runs is not earlier than the probe.
            probe.ObserveServer(serverBefore.AddMinutes(1), null, []);
            return Task.FromResult(new ProcessResult(0, "OK", ""));
        }, _ => "login-a");
        probe.ObserveServer(serverBefore, null, []);

        await probe.RefreshAsync([("codex", "gpt-6-sol")], CancellationToken.None);

        Assert.Equal(serverBefore, Assert.Single(probe.Snapshot("codex", ["gpt-6-sol"])).ProbeServerWatermark);
    }

    [Fact]
    public async Task Server_refusal_of_a_ready_verdict_reprobes_after_the_retry_interval()
    {
        var now = DateTimeOffset.UnixEpoch.AddHours(1);
        var calls = 0;
        var probe = new RunnerModelAcceptanceProbe();
        probe.Configure((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new ProcessResult(0, "OK", ""));
        }, _ => "login-a", () => now);
        await probe.RefreshAsync([("codex", "gpt-6-sol")], CancellationToken.None);
        var key = CapabilityProtocol.ModelAcceptance("codex", "gpt-6-sol");
        var held = new[] { new CapabilityHealthDto(key, "model-acceptance", "unavailable",
            CapabilityHealthStates.Healthy, null, now.UtcDateTime, now.UtcDateTime.AddMinutes(3), true,
            null, null, null, null, 0, null, null, null, [], [], CredentialHealth: null) };

        probe.ObserveServer(now.UtcDateTime, null, held);
        await probe.RefreshAsync([("codex", "gpt-6-sol")], CancellationToken.None);
        Assert.Equal(1, calls);

        now = now.AddMinutes(11);
        probe.ObserveServer(now.UtcDateTime, null, held);
        await probe.RefreshAsync([("codex", "gpt-6-sol")], CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.Equal(now.UtcDateTime,
            Assert.Single(probe.Snapshot("codex", ["gpt-6-sol"])).ProbeServerWatermark);
    }

    [Fact]
    public void Model_rejection_parser_requires_the_requested_model()
    {
        var result = new ProcessResult(1, "", "HTTP 400 {\"error\":{\"message\":\"The gpt-6-sol model is not supported when using Codex with a ChatGPT account\",\"type\":\"invalid_request_error\"}}");
        Assert.True(RunnerModelAcceptanceProbe.IsModelRejection(result, "gpt-6-sol"));
        Assert.False(RunnerModelAcceptanceProbe.IsModelRejection(result, "gpt-6-luna"));
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
