using AgentStudio.TaskServer.Contracts;
using System.Text.Json;
using Xunit;

namespace AgentRunner.Tests;

public sealed class MechanicalSessionContinuationTests
{
    private static readonly MechanicalRoundDelta Delta = new(
        new string('b', 40), "refs/heads/delivery", new string('a', 40),
        ["src/Feature.cs"], "Resolve the integration conflict.", "Run focused tests.");

    private static readonly SessionContinuationLedgerEntry Previous = new(
        "attempt-1", "AGT-1", "codex", "https://example.test/repo.git",
        "/work/AGT-1", "runner/host/AGT-1", Delta.DeliveryRef, Delta.DeliverySha,
        "host|/clean/AGT-1", null, "session-1", "fresh-run", null,
        false, 0, 100, 20, 0, 120, 60, DateTime.UtcNow);

    private static string? Decide(SessionContinuationLedgerEntry? previous = null,
        MechanicalRoundDelta? delta = null, string provider = "codex",
        string repository = "https://example.test/repo.git",
        string path = "/work/AGT-1", string branch = "runner/host/AGT-1",
        string? home = "host|/clean/AGT-1", bool sessionPresent = true)
        => MechanicalSessionResumePolicy.RejectionReason(
            previous ?? Previous, delta ?? Delta, "AGT-1", provider, repository,
            path, branch, home, sessionPresent);

    [Fact]
    public void Matching_lineage_permits_one_bounded_resume()
    {
        Assert.Null(Decide());
        Assert.Equal(1_211_213, MechanicalSessionResumePolicy.TokenCeiling);
        Assert.Equal(300, MechanicalSessionResumePolicy.DurationCeilingSeconds);
        Assert.Equal("continuation-limit-reached", Decide(previous: Previous with { MechanicalResumesUsed = 1 }));
    }

    [Theory]
    [InlineData("claude", "https://example.test/repo.git", "/work/AGT-1", "runner/host/AGT-1", "host|/clean/AGT-1", true, "provider-change")]
    [InlineData("codex", "https://example.test/other.git", "/work/AGT-1", "runner/host/AGT-1", "host|/clean/AGT-1", true, "repository-mismatch")]
    [InlineData("codex", "https://example.test/repo.git", "/work/other", "runner/host/AGT-1", "host|/clean/AGT-1", true, "worktree-path-mismatch")]
    [InlineData("codex", "https://example.test/repo.git", "/work/AGT-1", "runner/other/AGT-1", "host|/clean/AGT-1", true, "branch-mismatch")]
    [InlineData("codex", "https://example.test/repo.git", "/work/AGT-1", "runner/host/AGT-1", "other|/clean/AGT-1", true, "clean-context-mismatch")]
    [InlineData("codex", "https://example.test/repo.git", "/work/AGT-1", "runner/host/AGT-1", "host|/clean/AGT-1", false, "stale-session")]
    public void Mismatch_is_a_typed_fresh_run(string provider, string repository, string path,
        string branch, string home, bool sessionPresent, string reason)
        => Assert.Equal(reason, Decide(provider: provider, repository: repository,
            path: path, branch: branch, home: home, sessionPresent: sessionPresent));

    [Fact]
    public void Changed_delivery_and_missing_session_do_not_resume()
    {
        Assert.Equal("branch-lineage-mismatch",
            Decide(delta: Delta with { DeliverySha = new string('c', 40) }));
        Assert.Equal("missing-session", Decide(previous: Previous with { CapturedSessionId = null }));
    }

    [Fact]
    public void Usage_frames_use_the_specified_processed_token_measure()
    {
        Assert.True(MechanicalTokenUsage.TryRead(
            "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":300,\"cached_input_tokens\":200,\"output_tokens\":50}}",
            out var codex, out var cumulative));
        Assert.Equal(350, codex);
        Assert.False(cumulative);
        Assert.True(MechanicalTokenUsage.TryRead(
            "{\"type\":\"result\",\"usage\":{\"input_tokens\":300,\"cache_read_input_tokens\":200,\"output_tokens\":50}}",
            out var claude, out _));
        Assert.Equal(550, claude);
        Assert.True(MechanicalTokenUsage.TryRead(
            "{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":70,\"output_tokens\":5},\"total_token_usage\":{\"input_tokens\":5000,\"output_tokens\":500}}}",
            out var last, out var lastIsCumulative));
        Assert.Equal(75, last);
        Assert.False(lastIsCumulative);
    }

    [Fact]
    public void Worker_evidence_keeps_the_session_id_and_counts_both_process_generations()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mechanical-evidence-" + Guid.NewGuid().ToString("N"));
        var resumed = Path.Combine(directory, "resume-1");
        Directory.CreateDirectory(resumed);
        try
        {
            File.WriteAllLines(Path.Combine(directory, "output.jsonl"),
            [
                JsonSerializer.Serialize(new DetachedJobLogLine(1, DateTime.UtcNow, "stdout",
                    "{\"type\":\"thread.started\",\"thread_id\":\"session-1\"}")),
                JsonSerializer.Serialize(new DetachedJobLogLine(2, DateTime.UtcNow, "stdout",
                    "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":100,\"output_tokens\":20}}")),
            ]);
            File.WriteAllLines(Path.Combine(resumed, "output.jsonl"),
            [
                JsonSerializer.Serialize(new DetachedJobLogLine(1, DateTime.UtcNow, "stdout",
                    "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":50,\"output_tokens\":10}}")),
            ]);
            var evidence = SessionContinuationEvidence.ReadWorkerEvidence(resumed);
            Assert.Equal("session-1", evidence.SessionId);
            Assert.Equal(180, evidence.TotalTokens);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Cumulative_usage_is_attributed_to_the_current_generation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mechanical-cumulative-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllLines(Path.Combine(directory, "output.jsonl"),
            [JsonSerializer.Serialize(new DetachedJobLogLine(1, DateTime.UtcNow, "stdout",
                "{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":140,\"output_tokens\":60}}}"))]);
            Assert.Equal(200, SessionContinuationEvidence.ReadWorkerEvidence(directory).TotalTokens);
            Assert.Equal(75, SessionContinuationEvidence.ReadWorkerEvidence(directory, priorSessionTokens: 125).TotalTokens);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(RunOutcomeKind.Blocked, ExecutionOutcomeKind.ExplicitAgentBlocker, null, "semantic-conflict")]
    [InlineData(RunOutcomeKind.Unknown, ExecutionOutcomeKind.InvalidSession, null, "invalid-session-after-resume")]
    [InlineData(RunOutcomeKind.Unknown, ExecutionOutcomeKind.Timeout, "Resumed mechanical token ceiling reached", "resume-token-ceiling")]
    [InlineData(RunOutcomeKind.Unknown, ExecutionOutcomeKind.Timeout, "Runner timeout", "resume-duration-ceiling")]
    [InlineData(RunOutcomeKind.Unknown, ExecutionOutcomeKind.CliCrash, null, "resumed-round-inconclusive")]
    public void Resumed_failure_selects_a_typed_fresh_round(
        RunOutcomeKind outcome, ExecutionOutcomeKind typed, string? stderr, string reason)
    {
        Assert.Equal(reason, MechanicalRoundFallbackPolicy.Reason(true, outcome, typed, stderr));
        Assert.Null(MechanicalRoundFallbackPolicy.Reason(false, outcome, typed, stderr));
    }
}
