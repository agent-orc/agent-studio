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
