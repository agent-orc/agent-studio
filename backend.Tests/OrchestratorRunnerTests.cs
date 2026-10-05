
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the JSON-envelope parser <see cref="OrchestratorRunner"/> uses
/// to read <c>claude -p ... --output-format json</c> output. The CLI's
/// envelope shape is the only contract we depend on; pinning it here
/// keeps the runner safe against accidental field renames in a
/// <see cref="ParseResult"/> refactor.
/// </summary>
public class OrchestratorRunnerTests
{
    [Fact]
    public async Task DecideCodexAsync_MarksRequestAsOrchestratorChat()
    {
        var oneShot = new CapturingCodexOneShot();
        var runner = new OrchestratorRunner(
            logger: NullLogger<OrchestratorRunner>.Instance,
            oneShotRegistry: new CliOneShotRegistry([oneShot]));

        var result = await runner.DecideCodexAsync(
            "prompt", "gpt-5.5", "high", Path.GetTempPath());

        Assert.True(result.Success);
        Assert.Equal("orchestrator-chat", Assert.Single(oneShot.Requests).Source);
    }

    [Fact]
    public void ParseResult_HappyPath_ExtractsTextAndUsage()
    {
        // Real shape captured from `claude -p "..." --output-format json --model claude-haiku-4-5`.
        var json = """
        {
          "type": "result",
          "subtype": "success",
          "is_error": false,
          "duration_ms": 4321,
          "duration_api_ms": 4100,
          "num_turns": 1,
          "result": "Continue with the existing chat compose box implementation.",
          "session_id": "a1b2c3d4-e5f6-4789-abcd-ef0123456789",
          "total_cost_usd": 0.0123,
          "model": "claude-haiku-4-5",
          "usage": {
            "input_tokens": 1234,
            "cache_creation_input_tokens": 56,
            "cache_read_input_tokens": 789,
            "output_tokens": 42
          }
        }
        """;

        var result = OrchestratorRunner.ParseResult(json, "claude-haiku-4-5");

        Assert.True(result.Success);
        Assert.Equal("Continue with the existing chat compose box implementation.", result.ReplyText);
        Assert.Equal("claude-haiku-4-5", result.Model);
        Assert.NotNull(result.TokenUsage);
        Assert.Equal(1234, result.TokenUsage!.InputTokens);
        Assert.Equal(42, result.TokenUsage.OutputTokens);
        Assert.Equal(789, result.TokenUsage.CacheReadTokens);
        Assert.Equal(56, result.TokenUsage.CacheCreationTokens);
    }

    [Fact]
    public void ParseResult_IsErrorTrue_FlagsFailure()
    {
        var json = """{ "type": "result", "is_error": true, "result": "rate limit", "usage": { "input_tokens": 0, "output_tokens": 0 } }""";
        var result = OrchestratorRunner.ParseResult(json, "claude-opus-4-7");
        Assert.False(result.Success);
        Assert.Equal("rate limit", result.ReplyText);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void ParseResult_EmptyResultText_FlagsFailure()
    {
        var json = """{ "type": "result", "is_error": false, "result": "", "usage": { "input_tokens": 1, "output_tokens": 0 } }""";
        var result = OrchestratorRunner.ParseResult(json, "claude-opus-4-7");
        Assert.False(result.Success);
    }

    [Fact]
    public void ParseResult_MissingUsage_StillReturnsText()
    {
        var json = """{ "type": "result", "is_error": false, "result": "ok" }""";
        var result = OrchestratorRunner.ParseResult(json, "claude-opus-4-7");
        Assert.True(result.Success);
        Assert.Equal("ok", result.ReplyText);
        Assert.Null(result.TokenUsage);
    }

    [Fact]
    public void ParseResult_GarbageInput_FlagsFailure()
    {
        var result = OrchestratorRunner.ParseResult("not json at all", "claude-opus-4-7");
        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void ParseResult_EmptyStdout_FlagsFailure()
    {
        var result = OrchestratorRunner.ParseResult("", "claude-opus-4-7");
        Assert.False(result.Success);
    }

    /// <summary>
    /// Boot-bug regression: the prompt MUST NOT appear as an argv entry. It
    /// is piped via stdin so multi-KB markdown with newlines, backticks,
    /// double quotes, and Windows backslashes cannot break through cmd.exe's
    /// command-line length and quoting limits. Production failure mode was
    /// the CLI dropping --output-format from the args under the prompt blob,
    /// then returning prose ("I'll wait for...") that ParseResult rejected
    /// with "'I' is an invalid start of a value".
    /// </summary>
    [Fact]
    public void BuildArgs_NeverEmbedsPromptContent()
    {
        var (args, modelId) = OrchestratorRunner.BuildArgs("claude-opus-4-7", resumeSessionId: null);

        Assert.Equal("claude-opus-4-7", modelId);
        Assert.Contains("-p", args);
        Assert.Contains("--output-format", args);
        var idx = args.IndexOf("--output-format");
        Assert.Equal("json", args[idx + 1]);
        Assert.Contains("--dangerously-skip-permissions", args);

        // -p is always followed by another flag (no positional prompt arg).
        var pIdx = args.IndexOf("-p");
        Assert.True(args[pIdx + 1].StartsWith("--"),
            $"Expected -p to be a bare flag, got followed by: {args[pIdx + 1]}");
    }

    [Fact]
    public void BuildArgs_DefaultsModelWhenMissing()
    {
        var (_, modelId) = OrchestratorRunner.BuildArgs(null, null);
        Assert.Equal(OrchestratorRunner.DefaultModel, modelId);

        var (_, blank) = OrchestratorRunner.BuildArgs("   ", null);
        Assert.Equal(OrchestratorRunner.DefaultModel, blank);
    }

    [Fact]
    public void BuildArgs_AddsResumeFlagWhenSessionGiven()
    {
        var (args, _) = OrchestratorRunner.BuildArgs(
            "claude-opus-4-7",
            resumeSessionId: "a1b2c3d4-e5f6-4789-abcd-ef0123456789");

        var rIdx = args.IndexOf("-r");
        Assert.True(rIdx > 0, "expected -r flag");
        Assert.Contains("a1b2c3d4-e5f6-4789-abcd-ef0123456789", args[rIdx + 1]);
    }

    [Fact]
    public void BuildArgs_OmitsResumeWhenSessionMissing()
    {
        var (args, _) = OrchestratorRunner.BuildArgs("claude-opus-4-7", null);
        Assert.DoesNotContain("-r", args);

        var (blank, _) = OrchestratorRunner.BuildArgs("claude-opus-4-7", "   ");
        Assert.DoesNotContain("-r", blank);
    }

    private sealed class CapturingCodexOneShot : ICliOneShot
    {
        public string CliType => CliTypes.Codex;
        public List<CliOneShotRequest> Requests { get; } = [];

        public Task<CliOneShotResult> RunAsync(
            CliOneShotRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            var now = DateTime.UtcNow;
            return Task.FromResult(new CliOneShotResult(
                Ok: true,
                ExitCode: 0,
                Stdout: string.Empty,
                Stderr: string.Empty,
                Duration: TimeSpan.Zero,
                ParsedText: "ok",
                Usage: null,
                RichUsage: null,
                Latency: new AgentMessageLatency(now, null, now, null, 0),
                Error: null));
        }
    }
}
