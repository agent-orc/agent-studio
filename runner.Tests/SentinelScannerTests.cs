using AgentRunner;
using System.Text.Json;
using Xunit;

namespace AgentRunner.Tests;

public class SentinelScannerTests
{
    [Fact]
    public void Done_sentinel_is_recognised()
    {
        var outcome = SentinelScanner.Scan("all good\n[[TASK_DONE]]\n");
        Assert.Equal(RunOutcomeKind.Done, outcome.Kind);
        Assert.Null(outcome.Reason);
    }

    [Fact]
    public void Blocked_sentinel_carries_reason()
    {
        var outcome = SentinelScanner.Scan("[[TASK_BLOCKED: missing credentials]]");
        Assert.Equal(RunOutcomeKind.Blocked, outcome.Kind);
        Assert.Equal("missing credentials", outcome.Reason);
    }

    [Fact]
    public void Needs_input_underscore_and_dash_forms_normalise()
    {
        Assert.Equal(RunOutcomeKind.NeedsInput, SentinelScanner.Scan("[[TASK_NEEDS_INPUT: which env]]").Kind);
        Assert.Equal(RunOutcomeKind.NeedsInput, SentinelScanner.Scan("[[TASK NEEDS-INPUT]]").Kind);
    }

    [Fact]
    public void Last_sentinel_wins_matching_server_semantics()
    {
        var outcome = SentinelScanner.Scan("[[TASK_BLOCKED: early]]\nlater\n[[TASK_DONE]]");
        Assert.Equal(RunOutcomeKind.Done, outcome.Kind);
    }

    [Fact]
    public void Prose_mentioning_the_token_does_not_match()
    {
        var outcome = SentinelScanner.Scan("I will emit TASK_DONE when the work is finished.");
        Assert.Equal(RunOutcomeKind.Unknown, outcome.Kind);
    }

    [Fact]
    public void Missing_sentinel_is_unknown_and_routes_to_human_review()
    {
        var outcome = SentinelScanner.Scan("the CLI produced no sign-off");
        Assert.Equal(RunOutcomeKind.Unknown, outcome.Kind);
        Assert.Equal("5-human-review", outcome.TargetState);
    }

    [Fact]
    public void Agt2736_fixture_round_trips_verbatim_question_in_completion_envelope()
    {
        var transcript = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "needs-input", "agt-2736.codex.jsonl"));
        var outcome = SentinelScanner.Scan(transcript);

        Assert.Equal(RunOutcomeKind.NeedsInput, outcome.Kind);
        Assert.Contains("Which deployment strategy should I implement?", outcome.NeedsInputMessage);
        Assert.Contains("Option A: managed connector", outcome.NeedsInputMessage);
        Assert.Contains("Option B: direct LAN deployment", outcome.NeedsInputMessage);
        Assert.DoesNotContain("TASK_NEEDS_INPUT", outcome.NeedsInputMessage);

        var envelope = new RemoteRunCompletionRequest(
            "AGT-2736", "lease", 7, "agent-runner-01", "NeedsInput",
            Reason: outcome.Reason,
            SalvageBranch: "runner/agent-runner-01/AGT-2736",
            AttemptId: "run_4e53d6d6",
            NeedsInputMessage: outcome.NeedsInputMessage);
        var json = JsonSerializer.Serialize(envelope);
        var roundTrip = JsonSerializer.Deserialize<RemoteRunCompletionRequest>(json);

        Assert.Equal(outcome.NeedsInputMessage, roundTrip!.NeedsInputMessage);
        Assert.Equal("run_4e53d6d6", roundTrip.AttemptId);
        Assert.Equal("runner/agent-runner-01/AGT-2736", roundTrip.SalvageBranch);
    }

    [Fact]
    public void Needs_input_message_is_bounded_by_utf8_bytes_without_splitting_a_surrogate()
    {
        var outcome = SentinelScanner.Scan(
            $"Question? {string.Concat(Enumerable.Repeat("🧭", 10_000))}\n[[TASK_NEEDS_INPUT:bounded-question]]");

        Assert.Equal(RunOutcomeKind.NeedsInput, outcome.Kind);
        Assert.NotNull(outcome.NeedsInputMessage);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(outcome.NeedsInputMessage) <= 16 * 1024);
        Assert.StartsWith("Question? ", outcome.NeedsInputMessage);
    }
}
