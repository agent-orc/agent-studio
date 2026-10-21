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
    public async Task Launch_rejection_recorded_during_a_probe_wins_and_orders_later_evidence()
    {
        var now = DateTimeOffset.UnixEpoch.AddHours(1);
        var probe = new RunnerModelAcceptanceProbe();
        var generation = "login-a";
        var racingRejection = true;
        probe.Configure((provider, model, _) =>
        {
            // The coding run's refusal lands while this probe is in flight.
            if (racingRejection) probe.RecordRejection(provider, model);
            return Task.FromResult(new ProcessResult(0, "OK", ""));
        }, _ => generation, () => now);

        await probe.RefreshAsync([("codex", "gpt-6-sol")], CancellationToken.None);
        var held = Assert.Single(probe.Snapshot("codex", ["gpt-6-sol"]));
        Assert.Equal("unavailable", held.Status);

        racingRejection = false;
        generation = "login-b";
        var unverified = Assert.Single(probe.Snapshot("codex", ["gpt-6-sol"]));
        await probe.RefreshAsync([("codex", "gpt-6-sol")], CancellationToken.None);
        var recovered = Assert.Single(probe.Snapshot("codex", ["gpt-6-sol"]));

        Assert.Equal("unknown", unverified.Status);
        Assert.Equal("ready", recovered.Status);
        Assert.True(recovered.ProbeSequence > unverified.ProbeSequence);
        Assert.True(unverified.ProbeSequence >= held.ProbeSequence);
    }

    [Fact]
    public void Operator_configured_routes_outside_the_built_in_list_are_probe_candidates()
    {
        var options = new RunnerOptions
        {
            ServerUrl = "http://127.0.0.1:5031",
            RunnerId = "coding-a",
            RunnerName = "coding-a",
            Hostname = "host-a",
            BackendName = "test",
            Role = "coding",
            WorkDir = Path.GetTempPath(),
            BaseBranch = "main",
            CodexCliBin = Path.Combine(Path.GetTempPath(), $"missing-codex-{Guid.NewGuid():N}"),
            ClaudeCliBin = Path.Combine(Path.GetTempPath(), $"missing-claude-{Guid.NewGuid():N}"),
        };
        var probe = new RunnerModelAcceptanceProbe();
        probe.UseConfiguredModels(
        [
            new ConfiguredModelRouteDto("Codex", " gpt-6.2-sol "),
            new ConfiguredModelRouteDto("claude", "claude-opus-6[1m]"),
            new ConfiguredModelRouteDto("codex", "../escape"),
            new ConfiguredModelRouteDto("codex", "gpt 6"),
            new ConfiguredModelRouteDto("gemini", "gemini-3-pro"),
        ]);

        var candidates = RunnerCapabilityProbe.ModelCandidates(options, probe.ConfiguredModels);

        Assert.Contains(("codex", "gpt-6.2-sol"), candidates);
        Assert.Contains(("claude", "claude-opus-6[1m]"), candidates);
        Assert.Contains(("codex", "gpt-6-sol"), candidates);
        Assert.DoesNotContain(candidates, item => item.Model.Contains("escape", StringComparison.Ordinal)
                                                  || item.Model.Contains(' ') || item.Provider == "gemini");
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
