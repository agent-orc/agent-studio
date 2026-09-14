using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Frame-level regression tests for the Claude behavior's
/// <c>TransformReadLine</c> + <c>OnOutputLine</c>. Each test captures a
/// real <c>stream-json</c> frame shape (verified against the live
/// <c>claude</c> CLI) and pins the marker-line output we depend on.
///
/// Why this file exists: the rate-limit pill, the session-UUID capture,
/// and the activity-log marker classifier all sit on the precise text
/// shape these methods emit. Without per-frame regression tests, an
/// "innocent" tweak to <c>FormatToolUse</c> or the rate-limit kv tail
/// can silently break the live header pill or the Continue button. The
/// matching skill at <c>docs/system/cli/skills/cli-claude.md</c> documents the
/// frame catalogue these tests lock.
/// </summary>
public class ClaudeCliServiceTests
{
    private static GenericCliExecutionService NewService()
    {
        var cfg = new ConfigurationBuilder().Build();
        return GenericCliExecutionService.ForClaude(NullLogger<GenericCliExecutionService>.Instance, cfg);
    }

    private static CliOutputLine StdoutFrame(string json) => new()
    {
        Stream = "stdout",
        Text = json,
        Timestamp = DateTime.UtcNow
    };

    [Fact]
    public void IsCompatibleSessionName_AcceptsCanonicalUuidsRejectsEverythingElse()
    {
        var svc = NewService();

        Assert.True(svc.IsCompatibleSessionName("a1b2c3d4-e5f6-4789-abcd-ef0123456789"));
        Assert.False(svc.IsCompatibleSessionName(null));
        Assert.False(svc.IsCompatibleSessionName(""));
        // Slug from Copilot — must be rejected so `claude -r` never hangs.
        Assert.False(svc.IsCompatibleSessionName("taskboard-fix-bug-202604282114"));
        // Codex's foreign-style id (also UUID, but it's valid; cross-CLI guard
        // is structural, not value-based — we accept any 8-4-4-4-12 here.
        // The cross-CLI rejection happens via cliType matching upstream.)
        Assert.True(svc.IsCompatibleSessionName("01993b1d-5816-7950-9f04-e6c46e09cf72"));
    }

    [Fact]
    public void TransformReadLine_SystemInitFrameProducesSessionMarker()
    {
        // Real shape captured from `claude -p ... --output-format stream-json --verbose`.
        // The session marker is what `OnOutputLine` reads back via
        // `SessionMarkerRegex` — keep "● Session init <uuid>" stable.
        var svc = NewService();
        var raw = StdoutFrame("""{"type":"system","subtype":"init","session_id":"f807bd9e-d676-4f0f-9345-3c40b670c228"}""");

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        Assert.StartsWith("● Session init f807bd9e-d676-4f0f-9345-3c40b670c228", lines[0].Text);
    }

    [Fact]
    public void TransformReadLine_AssistantTextFrameYieldsTextLines()
    {
        var svc = NewService();
        var raw = StdoutFrame("""{"type":"assistant","message":{"content":[{"type":"text","text":"Hello\nWorld"}]}}""");

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Equal(2, lines.Count);
        Assert.Equal("Hello", lines[0].Text);
        Assert.Equal("World", lines[1].Text);
    }

    [Fact]
    public void TransformReadLine_AssistantThinkingFrameIsSuppressed()
    {
        // Extended-thinking content is filtered out of the visible buffer.
        // If a debug flag becomes useful, surface it explicitly — the default
        // must stay silent.
        var svc = NewService();
        var raw = StdoutFrame("""{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"Let me consider this..."}]}}""");

        Assert.Empty(svc.TransformReadLine(raw));
    }

    [Theory]
    // tool name → expected marker prefix. Locks `FormatToolUse`'s mapping to
    // the marker-line vocabulary the frontend's activity-log parser expects.
    [InlineData("Read",      "/tmp/foo.ts",  "file_path",      "● Read /tmp/foo.ts")]
    [InlineData("Write",     "/tmp/bar.ts",  "file_path",      "● Write /tmp/bar.ts")]
    [InlineData("Edit",      "/tmp/baz.ts",  "file_path",      "● Edit /tmp/baz.ts")]
    [InlineData("Glob",      "src/**/*.ts",  "pattern",        "● Search glob src/**/*.ts")]
    [InlineData("Grep",      "TODO",         "pattern",        "● Search TODO")]
    [InlineData("WebFetch",  "https://example.com", "url",     "● Fetch https://example.com")]
    [InlineData("WebSearch", "claude api docs", "query",       "● Search web claude api docs")]
    public void TransformReadLine_ToolUseMapsToMarker(string toolName, string value, string key, string expected)
    {
        var svc = NewService();
        var raw = StdoutFrame($$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"{{{toolName}}}","input":{"{{{key}}}":"{{{value}}}"}}]}}""");

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        Assert.Equal(expected, lines[0].Text);
    }

    [Fact]
    public void TransformReadLine_BashToolTrimsAndSingleLinesCommand()
    {
        var svc = NewService();
        // Bash commands are flattened to one line and capped at 200 chars.
        var raw = StdoutFrame("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"echo hello\nworld"}}]}}""");

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        Assert.StartsWith("● Run", lines[0].Text);
        // Exact whitespace handling is internal; the contract is "no embedded newline".
        Assert.DoesNotContain('\n', lines[0].Text);
    }

    [Fact]
    public void TransformReadLine_TodoWriteCollapsesToSingleMarker()
    {
        // Per-item Todo bodies are noisy; we collapse to a single marker.
        var svc = NewService();
        var raw = StdoutFrame("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"TodoWrite","input":{"todos":[{"content":"a","status":"pending","activeForm":"a"}]}}]}}""");

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        Assert.Equal("● Todo update", lines[0].Text);
    }

    [Fact]
    public void TransformReadLine_RateLimitEventEmitsHumanAndKvHalvesInOneLine()
    {
        // The kv tail is what `OnOutputLine` reads back into the typed
        // ClaudeRateLimitSnapshot. The human prefix is what the user reads
        // in the Activity Log. Both must coexist on a single line so the
        // marker-classifier groups them together.
        var svc = NewService();
        var raw = StdoutFrame("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","rateLimitType":"five_hour","resetsAt":1777393800,"overageStatus":"allowed","isUsingOverage":false}}""");

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        var text = lines[0].Text;
        Assert.StartsWith("● Rate limit", text);
        Assert.Contains("five-hour", text);   // human prefix uses "-" instead of "_"
        Assert.Contains("allowed", text);
        // Locked kv tail format: `OnOutputLine`'s RateLimitMarkerRegex pins on this.
        Assert.Matches(@"\[window=five_hour status=allowed resetsAt=1777393800 overage=allowed usingOverage=false\]", text);
    }

    [Fact]
    public void TransformReadLine_RateLimitEventKvTailMatchesSnapshotRegex()
    {
        // End-to-end shape lock: the kv tail emitted by TransformReadLine
        // must match the regex that OnOutputLine uses to read it back. Both
        // halves are tested through the public surface so a refactor of
        // either side surfaces here.
        var svc = NewService();

        var raw = StdoutFrame("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","rateLimitType":"five_hour","resetsAt":1777393800,"overageStatus":"allowed","isUsingOverage":false}}""");
        var transformed = svc.TransformReadLine(raw).ToList();

        Assert.Single(transformed);
        Assert.Matches(@"\[window=five_hour status=allowed resetsAt=1777393800 overage=allowed usingOverage=false\]", transformed[0].Text);
    }

    [Fact]
    public void TransformReadLine_RateLimitEventAcceptsSnakeCaseAndUnknownFields()
    {
        var svc = NewService();
        var raw = StdoutFrame("""{"type":"rate_limit_event","future":{"ignored":true},"rate_limit_info":{"rate_limit_type":"seven_day","status":"allowed_warning","resets_at":"1777393800","overage_status":"not_allowed","is_using_overage":"false","unknown_field":42}}""");

        var line = Assert.Single(svc.TransformReadLine(raw));

        Assert.StartsWith("● Rate limit", line.Text);
        Assert.Matches(@"\[window=seven_day status=allowed_warning resetsAt=1777393800 overage=not_allowed usingOverage=false\]", line.Text);
    }

    [Fact]
    public void TransformReadLine_RateLimitEventWithUnexpectedOptionalFieldTypesDoesNotThrow()
    {
        var svc = NewService();
        var raw = StdoutFrame("""{"type":"rate_limit_event","rate_limit_info":{"rateLimitType":{"future":"shape"},"status":["allowed"],"resetsAt":[],"overageStatus":17,"isUsingOverage":null}}""");

        var exception = Record.Exception(() => svc.TransformReadLine(raw).ToList());
        var line = Assert.Single(svc.TransformReadLine(raw));

        Assert.Null(exception);
        Assert.Matches(@"\[window=\? status=\? resetsAt=0 overage=- usingOverage=false\]", line.Text);
    }

    [Fact]
    public void TransformReadLine_ResultFrameSurfacesText()
    {
        var svc = NewService();
        var raw = StdoutFrame("""{"type":"result","subtype":"success","result":"Layout-Fix committed.","is_error":false}""");

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        Assert.Equal("Layout-Fix committed.", lines[0].Text);
    }

    [Fact]
    public void TransformReadLine_ResultFrameWithoutTextEmitsSubtypeMarker()
    {
        var svc = NewService();
        var raw = StdoutFrame("""{"type":"result","subtype":"success","is_error":false}""");

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        Assert.Equal("● Result (success)", lines[0].Text);
    }

    [Fact]
    public void TransformReadLine_StderrPassesThroughUntouched()
    {
        var svc = NewService();
        var raw = new CliOutputLine
        {
            Stream = "stderr",
            Text = "Error: something failed",
            Timestamp = DateTime.UtcNow
        };

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        Assert.Equal(raw, lines[0]);
    }

    [Fact]
    public void TransformReadLine_NonJsonStdoutPassesThroughUntouched()
    {
        var svc = NewService();
        var raw = StdoutFrame("Some plain status line");

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        Assert.Equal(raw, lines[0]);
    }

    [Fact]
    public void TransformReadLine_UnknownFrameTypeFallsBackToTypeMarker()
    {
        // New frame types should never leak raw JSON into the Activity Log
        // (the marker classifier downstream chokes on unprefixed JSON). The
        // catch-all renders `● <type>` so it stays parseable until a real
        // case is added.
        var svc = NewService();
        var raw = StdoutFrame("""{"type":"some_new_frame","payload":{"x":1}}""");

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Single(lines);
        Assert.Equal("● some_new_frame", lines[0].Text);
    }

    [Fact]
    public void TransformReadLine_ToolProgressHeartbeatIsDropped()
    {
        // tool_progress is a known Claude Code frame (CAR 0.7.0) carrying an
        // incremental tool-call delta. It is not narrative-worthy on its own,
        // so the renderer drops it instead of emitting a catch-all marker row.
        var svc = NewService();
        var raw = StdoutFrame("""{"type":"tool_progress","tool_use_id":"toolu_01","delta":"..."}""");

        var lines = svc.TransformReadLine(raw).ToList();

        Assert.Empty(lines);
    }

    [Theory]
    // Locks NormalizeModelId's dotted-to-dashed coercion. Users sometimes
    // type "claude-opus-4.7"; the Anthropic CLI requires "claude-opus-4-7".
    [InlineData("claude-opus-4.7",       "claude-opus-4-7")]
    [InlineData("claude-sonnet-4.6",     "claude-sonnet-4-6")]
    [InlineData("claude-haiku-4.5",      "claude-haiku-4-5")]
    // Already-dashed forms pass through unchanged.
    [InlineData("claude-opus-4-7",       "claude-opus-4-7")]
    // Non-claude ids pass through unchanged so custom ids still flow.
    [InlineData("custom-model-id",       "custom-model-id")]
    [InlineData(null,                    null)]
    [InlineData("",                      "")]
    [InlineData("   ",                   "   ")]
    public void NormalizeModelId_CoercesDottedFormToDashed(string? input, string? expected)
    {
        Assert.Equal(expected, BuiltInCliBehaviors.NormalizeModelId(input));
    }

    [Fact]
    public void BuildStartInfo_AddsEffortFlag_WhenModelSupportsThinkingLevel()
    {
        var svc = NewService();

        var args = svc.BuildStartInfoForTest(
            "do work",
            Environment.CurrentDirectory,
            sessionName: null,
            resumeSession: false,
            model: "claude-opus-4-8",
            thinkingLevel: "xhigh").ArgumentList.ToArray();

        Assert.Contains("--effort", args);
        Assert.Contains("xhigh", args);
    }

    [Fact]
    public void BuildStartInfo_OmitsEffortFlag_WhenModelHasNoThinkingLevel()
    {
        var svc = NewService();

        var args = svc.BuildStartInfoForTest(
            "do work",
            Environment.CurrentDirectory,
            sessionName: null,
            resumeSession: false,
            model: "claude-haiku-4-5",
            thinkingLevel: "high").ArgumentList.ToArray();

        Assert.DoesNotContain("--effort", args);
    }
}
