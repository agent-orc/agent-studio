using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>AGT-3063 runner-hosted shadow decision job.</summary>
public sealed class OrchestratorDecisionJobRunnerTests
{
    private static readonly DateTime At = new(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);
    private static readonly OrchestratorDecisionJobWork Work = new("odj_1", "claim-1", "prompt", "codex",
        "gpt-6-sol", "medium", 540, At.AddMinutes(10));

    [Fact]
    public void Codex_turn_reports_one_frame_keyed_by_thread_with_uncached_input()
    {
        var process = new ProcessResult(0, string.Join('\n',
            """{"type":"thread.started","thread_id":"thread-7"}""",
            """{"type":"item.completed","item":{"type":"agent_message","text":"{\"chosenActions\":[]}"}}""",
            """{"type":"turn.completed","usage":{"input_tokens":7000,"cached_input_tokens":6000,"output_tokens":200}}"""),
            "");
        var parsed = RemoteProjectChatRunner.ParseCodex(process, "gpt-6-sol");

        var completion = OrchestratorDecisionJobRunner.Completion(Work, "runner-one", "codex", parsed, At, At.AddSeconds(9));

        Assert.True(completion.Success);
        var frame = Assert.Single(completion.Usage);
        Assert.Equal(new DecisionUsageFrameV1("codex:thread-7", "gpt-6-sol", 1_000, 6_000, 0, 200, false), frame);
        Assert.False(completion.TransportFailure);
        Assert.Equal(("odj_1", "claim-1", "runner-one"), (completion.JobId, completion.ClaimToken, completion.RunnerId));
    }

    [Fact]
    public void Failure_without_any_usage_frame_is_a_transport_failure()
    {
        var parsed = RemoteProjectChatRunner.ParseClaude(new ProcessResult(1, "", "connection reset"), "claude-opus-5-5");

        var completion = OrchestratorDecisionJobRunner.Completion(Work, "runner-one", "claude", parsed, At, At);

        Assert.False(completion.Success);
        Assert.Empty(completion.Usage);
        Assert.True(completion.TransportFailure);
        Assert.Equal("connection reset", completion.Error);
    }

    [Fact]
    public async Task Admission_executes_one_claimed_job_and_reports_an_empty_queue()
    {
        var executed = new List<string>();
        var claims = 0;
        var admission = new OrchestratorDecisionJobAdmission(
            _ => Task.FromResult(++claims == 1
                ? new OrchestratorDecisionJobClaimResponse(DecisionJobClaimStatuses.Claimed, Work)
                : new OrchestratorDecisionJobClaimResponse(DecisionJobClaimStatuses.Empty)),
            (work, _) => { executed.Add(work.JobId); return Task.FromResult(0); },
            _ => { });

        Assert.True(await admission.PollOnceAsync(default));
        Assert.False(await admission.PollOnceAsync(default));
        Assert.Equal(["odj_1"], executed);
    }
}
