using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RemoteCompletionProtocolTests
{
    private const string ValidBaseSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ValidResultSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ValidManifestDigest =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private static WorktreeTeardownResult SecuredTeardown(
        string? resultSha = ValidResultSha,
        string? immutableResultRef = "refs/heads/agent-studio/results/attempt-1/" + ValidResultSha)
        => new(true, "runner/host/AGT-1", resultSha, null,
            ResultSha: resultSha,
            ImmutableResultRef: immutableResultRef);

    [Fact]
    public void Completion_carries_the_envelope_trio_when_every_field_is_valid()
    {
        var (baseSha, resultRef, manifestDigest) = RemoteTaskRunner.BuildEnvelopeCompletionFields(
            SecuredTeardown(), ValidBaseSha, ValidManifestDigest);

        Assert.Equal(ValidBaseSha, baseSha);
        Assert.StartsWith("refs/heads/agent-studio/results/", resultRef);
        Assert.Equal(ValidManifestDigest, manifestDigest);
    }

    [Fact]
    public void Missing_immutable_result_ref_omits_the_trio_for_server_side_unverified_routing()
    {
        var (baseSha, resultRef, manifestDigest) = RemoteTaskRunner.BuildEnvelopeCompletionFields(
            SecuredTeardown(immutableResultRef: null), ValidBaseSha, ValidManifestDigest);

        Assert.Null(baseSha);
        Assert.Null(resultRef);
        Assert.Null(manifestDigest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-sha")]
    [InlineData("abc123")]
    public void Invalid_base_sha_suppresses_the_whole_trio(string? baseSha)
    {
        var fields = RemoteTaskRunner.BuildEnvelopeCompletionFields(
            SecuredTeardown(), baseSha, ValidManifestDigest);

        Assert.Equal((null, null, null), fields);
    }

    [Fact]
    public void Invalid_manifest_digest_suppresses_the_whole_trio()
    {
        var fields = RemoteTaskRunner.BuildEnvelopeCompletionFields(
            SecuredTeardown(), ValidBaseSha, "zz" + ValidManifestDigest[2..]);

        Assert.Equal((null, null, null), fields);
    }

    [Fact]
    public void No_work_teardown_never_produces_envelope_fields()
    {
        var fields = RemoteTaskRunner.BuildEnvelopeCompletionFields(
            WorktreeTeardownResult.NoWork, ValidBaseSha, ValidManifestDigest);

        Assert.Equal((null, null, null), fields);
    }

    [Fact]
    public void Daemon_prompt_adds_the_terminal_contract_to_the_task_prompt()
    {
        var prompt = RemoteRunPrompt.Build("Make the requested trivial change.");

        Assert.StartsWith("Make the requested trivial change.", prompt);
        Assert.DoesNotContain("docs/system/domains/model-routing-policy.md", prompt);
        Assert.DoesNotContain("docs/start/contribution-and-style-guide.html", prompt);
        Assert.Contains("MUST end with exactly one", prompt);
        Assert.Contains("[[TASK_DONE]]", prompt);
        Assert.Contains("[[TASK_BLOCKED:missing-dependency-xyz]]", prompt);
        Assert.Contains("[[TASK_NEEDS_INPUT:choose-primary-column]]", prompt);
        Assert.Contains("Replace the example reason", prompt);
        Assert.DoesNotContain("<reason>", prompt);
        Assert.Contains("[[TASK_NOOP]]", prompt);
        Assert.EndsWith(Environment.NewLine, prompt);
    }

    [Fact]
    public void Daemon_prompt_keeps_authored_prompt_then_appends_composed_mode_framing()
    {
        const string authored = "# Original\n\nOperator-authored text.";
        const string baseModeFraming = "## Planning mode\n\nProduce a plan before implementation.";
        const string enrichment =
            "## Prompt enrichment\n\n> Appended by the task server.\n\n- **Use the style guide** (`style-guide:frontend`)";
        var composedModeFraming = baseModeFraming + "\n\n" + enrichment + "\n\n";

        var prompt = RemoteRunPrompt.Build(
            authored,
            modeFraming: composedModeFraming,
            resultsDirectory: null);

        Assert.StartsWith(authored, prompt, StringComparison.Ordinal);
        Assert.True(prompt.IndexOf(baseModeFraming, StringComparison.Ordinal)
                    < prompt.IndexOf(enrichment, StringComparison.Ordinal));
        Assert.True(prompt.IndexOf(enrichment, StringComparison.Ordinal)
                    < prompt.IndexOf(RemoteRunPrompt.CompletionProtocol, StringComparison.Ordinal));
        Assert.Equal(1, Count(prompt, enrichment));
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    [Fact]
    public void Codex_exec_jsonl_done_event_is_recognized_as_a_regular_done_outcome()
    {
        const string codex0144Output = """
            {"type":"thread.started","thread_id":"019c"}
            {"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"Implemented and verified the trivial change.\n[[TASK_DONE]]"}}
            {"type":"turn.completed","usage":{"input_tokens":100,"output_tokens":20}}
            """;

        var outcome = SentinelScanner.Scan(codex0144Output);

        Assert.Equal(RunOutcomeKind.Done, outcome.Kind);
        Assert.Null(outcome.Reason);
        Assert.Equal("Remote run completed", outcome.SummaryPrefix);
    }

    [Fact]
    public void Last_terminal_event_wins_in_codex_jsonl_output()
    {
        const string output = """
            {"type":"item.completed","item":{"type":"agent_message","text":"[[TASK_NEEDS_INPUT:first question]]"}}
            {"type":"item.completed","item":{"type":"agent_message","text":"Question resolved.\n[[TASK_DONE]]"}}
            """;

        Assert.Equal(RunOutcomeKind.Done, SentinelScanner.Scan(output).Kind);
    }

    [Fact]
    public void Agt2208_sentinel_literal_in_streamed_diff_does_not_create_a_verdict()
    {
        const string output = """
            {"type":"thread.started","thread_id":"019c"}
            {"type":"item.completed","item":{"type":"command_execution","command":"git diff","aggregated_output":"+ const fixture = \"[[TASK_BLOCKED:missing API key]]\";\n","exit_code":0}}
            {"type":"turn.completed","usage":{"input_tokens":100,"output_tokens":20}}
            """;

        var outcome = SentinelScanner.Scan(output);

        Assert.Equal(RunOutcomeKind.Unknown, outcome.Kind);
        Assert.Null(outcome.Reason);
    }

    [Fact]
    public void Sentinel_literal_in_code_block_without_terminal_signoff_does_not_create_a_verdict()
    {
        const string output = """
            {"type":"item.completed","item":{"type":"agent_message","text":"Added this regression fixture:\n```text\n[[TASK_BLOCKED:missing API key]]\n```\nThe test now covers the stream parser."}}
            {"type":"turn.completed","usage":{"input_tokens":100,"output_tokens":20}}
            """;

        var outcome = SentinelScanner.Scan(output);

        Assert.Equal(RunOutcomeKind.Unknown, outcome.Kind);
    }

    [Fact]
    public void Sentinel_at_end_of_final_agent_message_remains_authoritative()
    {
        const string output = """
            {"type":"item.completed","item":{"type":"agent_message","text":"Implemented and verified the scanner regression.\n[[TASK_DONE]]"}}
            {"type":"item.completed","item":{"type":"command_execution","command":"git diff","aggregated_output":"+ const fixture = \"[[TASK_BLOCKED:missing API key]]\";\n","exit_code":0}}
            {"type":"turn.completed","usage":{"input_tokens":100,"output_tokens":20}}
            """;

        var outcome = SentinelScanner.Scan(output);

        Assert.Equal(RunOutcomeKind.Done, outcome.Kind);
        Assert.Null(outcome.Reason);
    }

    [Fact]
    public void Claude_completion_result_is_scanned_instead_of_tool_result_frames()
    {
        const string output = """
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"fixture.txt"}}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","content":"[[TASK_BLOCKED:missing API key]]"}]}}
            {"type":"result","subtype":"success","is_error":false,"result":"Implemented and verified the fix.\n[[TASK_DONE]]"}
            """;

        var outcome = SentinelScanner.Scan(output);

        Assert.Equal(RunOutcomeKind.Done, outcome.Kind);
    }
}
