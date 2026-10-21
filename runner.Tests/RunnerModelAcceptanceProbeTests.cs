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
    public async Task Operator_override_outside_the_curated_list_is_probed_and_advertised()
    {
        var now = DateTimeOffset.UnixEpoch.AddHours(1);
        var probed = new List<(string Provider, string Model)>();
        var probe = new RunnerModelAcceptanceProbe();
        probe.Configure((provider, model, _) =>
        {
            probed.Add((provider, model));
            return Task.FromResult(new ProcessResult(0, "OK", ""));
        }, _ => "login-a", () => now);
        var options = new RunnerOptions
        {
            ServerUrl = "http://localhost",
            RunnerId = "runner",
            RunnerName = "runner",
            Hostname = "host",
            BackendName = "test",
            WorkDir = Path.GetTempPath(),
            BaseBranch = "main",
        };

        // The configured override is not in the Codex catalogue either; a
        // malformed or unknown-provider entry is ignored.
        probe.AdoptConfiguredCandidates(
            ["codex/GPT-6.2-Sol-Preview", "claude/claude-opus-6", "codex/bad model", "gemini/gemini-4"]);
        var candidates = RunnerCapabilityProbe.ModelCandidates(options, probe, _ => ["gpt-6-luna"]);

        Assert.Contains(("codex", "gpt-6.2-sol-preview"), candidates);
        Assert.Contains(("claude", "claude-opus-6"), candidates);
        Assert.Contains(("codex", "gpt-6-luna"), candidates);
        Assert.DoesNotContain(("codex", "gpt-6-sol"), candidates);
        Assert.DoesNotContain(candidates, item => item.Model.Contains(' ') || item.Provider == "gemini");

        await probe.RefreshAsync(candidates, CancellationToken.None);
        Assert.Contains(("codex", "gpt-6.2-sol-preview"), probed);
        var advertised = Assert.Single(probe.Snapshot("codex", ["gpt-6.2-sol-preview"]));
        Assert.Equal(CapabilityProtocol.ModelAcceptance("codex", "gpt-6.2-sol-preview"), advertised.Key);
        Assert.Equal("ready", advertised.Status);
    }

    [Fact]
    public async Task Launch_rejection_takes_a_probe_sequence_that_only_a_later_probe_exceeds()
    {
        var generation = "login-a";
        var probe = new RunnerModelAcceptanceProbe();
        probe.Configure((_, _, _) => Task.FromResult(new ProcessResult(0, "OK", "")),
            _ => generation, () => DateTimeOffset.UnixEpoch.AddHours(1));
        await probe.RefreshAsync([("codex", "gpt-6-sol")], CancellationToken.None);
        var accepted = Sequence();

        probe.RecordRejection("codex", "gpt-6-sol");
        var rejected = Assert.Single(probe.Snapshot("codex", ["gpt-6-sol"]));
        Assert.Equal("unavailable", rejected.Status);
        Assert.True(Sequence() > accepted);
        var rejectedSequence = Sequence();

        generation = "login-b";
        await probe.RefreshAsync([("codex", "gpt-6-sol")], CancellationToken.None);
        Assert.Equal("ready", Assert.Single(probe.Snapshot("codex", ["gpt-6-sol"])).Status);
        Assert.True(Sequence() > rejectedSequence);

        long Sequence() => CapabilityProtocol.ModelProbeSequence(
            Assert.Single(probe.Snapshot("codex", ["gpt-6-sol"])).EvidenceId)!.Value;
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
