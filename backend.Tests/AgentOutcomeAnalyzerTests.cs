

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the deterministic-signal contract between agent and orchestrator.
/// The signals analyzed here are the load-bearing piece of the bigger
/// orchestration philosophy (deterministic parsing over prompt trust):
/// when a hard sentinel fires, the orchestrator treats it as authoritative;
/// when no sentinel fires, the analyzer must signal that the verdict is a
/// heuristic so <see cref="RunOutcomePolicy"/> can warn the user.
/// </summary>
public class AgentOutcomeAnalyzerTests
{
    private static List<CliOutputLine> Lines(params string[] texts)
    {
        var ts = new DateTime(2026, 5, 2, 10, 0, 0, DateTimeKind.Utc);
        return texts.Select((t, i) => new CliOutputLine
        {
            Timestamp = ts.AddSeconds(i),
            Stream = "stdout",
            Text = t
        }).ToList();
    }

    [Fact]
    public void Sentinel_Done_IsAuthoritativeAndOverridesHeuristic()
    {
        var lines = Lines("I tried but I cannot find the file.", "[[TASK_DONE]]");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "completed", durationSeconds: 22.5);
        Assert.Equal(AgentOutcomeKind.Done, outcome.Kind);
        Assert.True(outcome.MatchedSentinel);
        Assert.Equal("DONE", outcome.SentinelKeyword);
    }

    [Fact]
    public void Sentinel_Blocked_CapturesReason()
    {
        var lines = Lines("Some text.", "[[TASK_BLOCKED:missing credentials]]");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "completed", 12.0);
        Assert.Equal(AgentOutcomeKind.Blocked, outcome.Kind);
        Assert.True(outcome.MatchedSentinel);
        Assert.Equal("missing credentials", outcome.Reason);
    }

    [Fact]
    public void Sentinel_NeedsInput_Recognised()
    {
        var lines = Lines("[[TASK_NEEDS_INPUT:please pick A or B]]");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "completed", 5.0);
        Assert.Equal(AgentOutcomeKind.NeedsInput, outcome.Kind);
        Assert.True(outcome.MatchedSentinel);
    }

    [Fact]
    public void Local_transcript_preserves_question_and_options_before_needs_input_sentinel()
    {
        var message = "Which deployment should I implement?\n\n- A: connector-first (recommended)\n- B: direct LAN\n\nReply A or B to unblock me.";
        var outcome = AgentOutcomeAnalyzer.Analyze(
            Lines(message, "[[TASK_NEEDS_INPUT:choose-deployment]]"),
            "completed",
            30.0);

        Assert.Equal(AgentOutcomeKind.NeedsInput, outcome.Kind);
        Assert.Equal(message, outcome.NeedsInputMessage);
    }

    [Fact]
    public void Sentinel_Noop_Recognised()
    {
        var lines = Lines("Nothing to do.", "[[TASK_NOOP: already implemented]]");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "completed", 4.0);
        Assert.Equal(AgentOutcomeKind.NoOp, outcome.Kind);
        Assert.True(outcome.MatchedSentinel);
        Assert.Equal("already implemented", outcome.Reason);
        Assert.Equal(RunIssueKind.None, outcome.IssueKind);
    }

    [Fact]
    public void NoOutput_ShortDuration_ClassifiesEmptyFastExit()
    {
        // The exact failure shape the user reported: backend ran for 4.6s and
        // produced nothing. This is a failed start, not an agent no-op.
        var lines = new List<CliOutputLine>();
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "completed", 4.6, exitCode: 0);
        Assert.Equal(AgentOutcomeKind.Unknown, outcome.Kind);
        Assert.False(outcome.MatchedSentinel);
        Assert.Equal(RunIssueKind.EmptyFastExit, outcome.IssueKind);
        Assert.Equal(0, outcome.AgentTextChars);
        Assert.Contains("exitCode=0", outcome.Summary ?? string.Empty);
        Assert.Contains("failed start", outcome.Summary ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoOutput_LongDuration_ClassifiesUnknown()
    {
        // 60 s with no output isn't strictly a no-op (it could be a CLI that
        // suppressed everything); treat as unknown so the policy surfaces a
        // heuristic warning instead of silently re-issuing.
        var outcome = AgentOutcomeAnalyzer.Analyze(new List<CliOutputLine>(), "completed", 60.0);
        Assert.Equal(AgentOutcomeKind.Unknown, outcome.Kind);
        Assert.False(outcome.MatchedSentinel);
    }

    [Fact]
    public void Heuristic_DoneTextWithoutSentinel_FlagsFallback()
    {
        var lines = Lines("Implemented the feature and committed the change.");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "completed", 30.0);
        Assert.Equal(AgentOutcomeKind.Done, outcome.Kind);
        Assert.False(outcome.MatchedSentinel);
        Assert.Contains("heuristic", outcome.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Heuristic_TerminalQuestion_ClassifiesNeedsInput()
    {
        var lines = Lines("Should I switch the tab to Markdown mode first?");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "completed", 12.0);
        Assert.Equal(AgentOutcomeKind.NeedsInput, outcome.Kind);
        Assert.False(outcome.MatchedSentinel);
    }

    [Fact]
    public void SystemAndUserStreams_AreIgnored()
    {
        var ts = DateTime.UtcNow;
        var lines = new List<CliOutputLine>
        {
            new() { Timestamp = ts, Stream = "user", Text = "please continue" },
            new() { Timestamp = ts, Stream = "system", Text = "[taskboard] Started claude CLI" },
            new() { Timestamp = ts, Stream = "orchestrator", Text = "[reissue] previous run no-op'd" }
        };
        // No agent text and a short duration => failed start, not NoOp.
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "completed", 4.6);
        Assert.Equal(AgentOutcomeKind.Unknown, outcome.Kind);
        Assert.Equal(RunIssueKind.EmptyFastExit, outcome.IssueKind);
        Assert.Equal(0, outcome.AgentTextChars);
    }

    [Fact]
    public void StderrOnly_QuotaFastExit_IsEmptyFastExitWithDiagnostics()
    {
        var lines = new List<CliOutputLine>
        {
            new() { Timestamp = DateTime.UtcNow, Stream = "stderr", Text = "quota exceeded: rate limit reset later" }
        };

        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "completed", 1.2, exitCode: 0);

        Assert.Equal(AgentOutcomeKind.Unknown, outcome.Kind);
        Assert.Equal(RunIssueKind.EmptyFastExit, outcome.IssueKind);
        Assert.Equal(0, outcome.AgentTextChars);
        Assert.Contains("marker=quota-or-rate-limit", outcome.Summary ?? string.Empty);
        Assert.Contains("firstOutput=quota exceeded", outcome.Summary ?? string.Empty);
    }

    [Fact]
    public void Failed_Status_DoesNotClassifyNoOp()
    {
        var outcome = AgentOutcomeAnalyzer.Analyze(new List<CliOutputLine>(), "failed", 4.6);
        Assert.NotEqual(AgentOutcomeKind.NoOp, outcome.Kind);
    }

    [Fact]
    public void PermissionDenied_Output_IsClassifiedAsPermissionBlocked()
    {
        var lines = Lines(
            "x List workspace projects (shell)",
            "  | Get-ChildItem C:\\Projects\\agent-taskboard-workspace\\projects -Directory",
            "  └ Permission denied and could not request permission from user");

        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "completed", 64.0);

        Assert.Equal(RunIssueKind.PermissionBlocked, outcome.IssueKind);
        Assert.False(outcome.MatchedSentinel);
        Assert.Contains("permission", outcome.Summary ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SilentCompletionMarker_Output_IsClassifiedAsSilentCompletion()
    {
        // Mirror of the [environment-blocker] path: the analyzer trusts
        // the synthetic [codex-silent-completion] marker the runner wrote
        // when CodexSilentCompletionDetector tripped, because the gating
        // happened one layer up and the needle cannot appear elsewhere
        // without the runtime detector having fired.
        var ts = DateTime.UtcNow;
        var lines = new List<CliOutputLine>
        {
            new() { Timestamp = ts, Stream = "stdout", Text = "I edited some files." },
            new() { Timestamp = ts.AddSeconds(2), Stream = "system",
                Text = "[codex-silent-completion] Codex stopped after final tool call without a closing sentinel (silence=92s)" }
        };
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "completed", durationSeconds: 320.0);
        Assert.Equal(RunIssueKind.SilentCompletion, outcome.IssueKind);
        Assert.Equal(AgentOutcomeKind.Done, outcome.Kind);
        Assert.False(outcome.MatchedSentinel);
        Assert.Contains("sentinel", outcome.Summary ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WatchdogKilled_Output_IsClassifiedAsWatchdogTimeout()
    {
        var lines = new List<CliOutputLine>
        {
            new() { Timestamp = DateTime.UtcNow, Stream = "orchestrator", Text = "[giveup] [watchdog] Killed after 60s of silence. Process tree terminated; the run will finalize as failed." }
        };

        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "failed", 60.0);

        Assert.Equal(RunIssueKind.WatchdogTimeout, outcome.IssueKind);
        Assert.Equal(AgentOutcomeKind.Unknown, outcome.Kind);
        Assert.False(outcome.MatchedSentinel);
    }

    [Fact]
    public void WatchdogAutoCancelled_OperatorFriendlyForm_IsClassifiedAsWatchdogTimeout()
    {
        // The Hung state now writes the operator-friendly form
        // `[watchdog-timeout] "title" (cli): auto-cancelled after Ns ...`.
        // The classifier must still mark the run as WatchdogTimeout so the
        // outcome policy treats it the same as the legacy `[watchdog] Killed`
        // shape; otherwise the new wording would silently demote watchdog
        // kills to MissingTerminalSentinel.
        var lines = new List<CliOutputLine>
        {
            new()
            {
                Timestamp = DateTime.UtcNow,
                Stream = "orchestrator",
                Text = "[watchdog-timeout] \"fix-git-diff-container-display\" (claude): auto-cancelled after 180s of silence. The run will finalize as failed."
            }
        };

        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "failed", 180.0);

        Assert.Equal(RunIssueKind.WatchdogTimeout, outcome.IssueKind);
        Assert.False(outcome.MatchedSentinel);
    }

    [Fact]
    public void CompletedTextWithoutSentinel_IsClassifiedAsMissingTerminalSentinel()
    {
        var lines = Lines("I checked the implementation and it looks ready for review.");

        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "completed", 22.0);

        Assert.Equal(RunIssueKind.MissingTerminalSentinel, outcome.IssueKind);
        Assert.Equal(AgentOutcomeKind.Done, outcome.Kind);
        Assert.False(outcome.MatchedSentinel);
    }

    // ---- Case (a): CLI / launch / resume failure --------------------------
    // A resume run that fails the instant it starts (exit != 0, ~0s) and
    // leaves only a CLI error fragment behind must NOT become a terminal
    // classifier-unknown FAILURE. It is a host/CLI failure, routed to the
    // typed CliLaunchFailed issue so the policy rebuilds from disk. This is
    // the exact ASS-755 shape: status=failed, duration=0.0s, only a
    // truncated CLI fragment as "agent" text.

    [Fact]
    public void FailedResume_ZeroDuration_OnlyCliFragment_IsCliLaunchFailed()
    {
        // The truncated codex fragment the user saw ("...orchestrator)").
        var lines = Lines("hestrator)");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "failed", durationSeconds: 0.0);
        Assert.Equal(RunIssueKind.CliLaunchFailed, outcome.IssueKind);
        Assert.NotEqual(RunIssueKind.OrchestratorInconclusive, outcome.IssueKind);
        Assert.NotEqual(RunIssueKind.InfraCrash, outcome.IssueKind);
        Assert.False(outcome.MatchedSentinel);
    }

    [Fact]
    public void FailedResume_RejectedResumeTargetNeedle_IsCliLaunchFailed_RegardlessOfDuration()
    {
        // The capture-fail decision line can leak into the consolidated buffer.
        // The needle is definitive even if the duration is not near-zero.
        var lines = new List<CliOutputLine>
        {
            new() { Timestamp = DateTime.UtcNow, Stream = "stdout", Text = "starting up" },
            new() { Timestamp = DateTime.UtcNow.AddSeconds(1), Stream = "system",
                Text = "[capture-fail] codex rejected the resume target (abc-123); next follow-up will rebuild from disk via Recovery." }
        };
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "failed", durationSeconds: 12.0);
        Assert.Equal(RunIssueKind.CliLaunchFailed, outcome.IssueKind);
    }

    [Fact]
    public void FailedResume_NoConversationFound_ClaudeNeedle_IsCliLaunchFailed()
    {
        var lines = Lines("No conversation found with session ID: 11111111-2222-3333-4444-555555555555");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "failed", durationSeconds: 1.0);
        Assert.Equal(RunIssueKind.CliLaunchFailed, outcome.IssueKind);
    }

    // ---- Case (b): failed run WITH a real agent turn ----------------------
    // A run that produced a genuine, substantial agent turn before failing is
    // NOT a launch failure - it is an unclassifiable agent reply. With no hard
    // process-death signal (exitCode not < 0) it is OrchestratorInconclusive,
    // so the policy stops and hands the task to the user (never a CLI launch
    // failure), and the analyzer must not swallow it into the launch bucket.

    [Fact]
    public void FailedRun_WithRealAgentText_IsOrchestratorInconclusive_NotCliLaunchFailed()
    {
        var prose = new string('x', 400) + " I made several edits and ran a long investigation across the module.";
        var lines = Lines(prose);
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "failed", durationSeconds: 120.0);
        Assert.Equal(RunIssueKind.OrchestratorInconclusive, outcome.IssueKind);
        Assert.NotEqual(RunIssueKind.CliLaunchFailed, outcome.IssueKind);
    }

    // ---- Case (b'): failed run WITH a real agent turn AND hard process death
    // The same substantial agent turn, but the CLI process was killed
    // (exitCode < 0, e.g. Windows Process.Kill returns -1) before reaching a
    // terminal verdict. That is infrastructure death, not an inconclusive
    // reply, so it discriminates to InfraCrash.

    [Fact]
    public void FailedRun_WithRealAgentText_AndNegativeExitCode_IsInfraCrash()
    {
        var prose = new string('x', 400) + " I made several edits and ran a long investigation across the module.";
        var lines = Lines(prose);
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "failed", durationSeconds: 120.0, exitCode: -1);
        Assert.Equal(RunIssueKind.InfraCrash, outcome.IssueKind);
        Assert.NotEqual(RunIssueKind.CliLaunchFailed, outcome.IssueKind);
        Assert.NotEqual(RunIssueKind.OrchestratorInconclusive, outcome.IssueKind);
    }

    // ---- AGT-2066 WÄCHTER: OAuth-refresh launch failure -------------------
    // The exact incident signature: a claude launch dies with "OAuth session
    // expired and could not be refreshed". This is a dead/rotated shared token
    // no re-issue can revive, so it must be the typed, NON-RETRYABLE
    // AuthRefreshFailed - NOT the generic CliLaunchFailed, which would rebuild
    // from disk and RETRY (burning a launch budget per card, 17 cards in the
    // 2026-07-10 incident).

    [Fact]
    public void FailedLaunch_OAuthSessionExpired_IsAuthRefreshFailed_NotCliLaunchFailed()
    {
        var lines = Lines("OAuth session expired and could not be refreshed");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "failed", durationSeconds: 0.4);
        Assert.Equal(RunIssueKind.AuthRefreshFailed, outcome.IssueKind);
        Assert.NotEqual(RunIssueKind.CliLaunchFailed, outcome.IssueKind);
        Assert.False(outcome.MatchedSentinel);
    }

    [Fact]
    public void FailedLaunch_CouldNotBeRefreshedNeedle_IsAuthRefreshFailed_RegardlessOfDuration()
    {
        // The needle is definitive even when the run did not die near-instantly,
        // so it wins over the generic launch-failure duration heuristic.
        var lines = Lines("Refreshing credentials...", "Error: the credentials could not be refreshed.");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "failed", durationSeconds: 8.0);
        Assert.Equal(RunIssueKind.AuthRefreshFailed, outcome.IssueKind);
    }

    [Fact]
    public void HealthyRun_MentioningRefresh_IsNotAuthRefreshFailed()
    {
        // Gating on `failed` keeps a completed run that merely discusses token
        // refresh in its prose from tripping the breaker.
        var lines = Lines("I checked how the CLI handles a token that could not be refreshed and documented it. [[TASK_DONE]]");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "completed", durationSeconds: 42.0);
        Assert.NotEqual(RunIssueKind.AuthRefreshFailed, outcome.IssueKind);
        Assert.True(outcome.MatchedSentinel);
        Assert.Equal(AgentOutcomeKind.Done, outcome.Kind);
    }

    // ---- Case (c): successful run, no sentinel, inconclusive text ---------
    // A clean exit whose text the heuristic cannot map to any shape stays
    // MissingTerminalSentinel so the orchestrator drives it to a structured
    // close-out, never a terminal inconclusive FAILURE.

    [Fact]
    public void SuccessfulRun_InconclusiveText_IsMissingTerminalSentinel()
    {
        var lines = Lines("The weather over the harbour was unusually calm this morning.");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "completed", durationSeconds: 18.0);
        Assert.Equal(AgentOutcomeKind.Unknown, outcome.Kind);
        Assert.Equal(RunIssueKind.MissingTerminalSentinel, outcome.IssueKind);
        Assert.NotEqual(RunIssueKind.OrchestratorInconclusive, outcome.IssueKind);
    }

    // ---- Tolerant sentinel recognition (ASS-643) --------------------------
    // The systemic classifier-unknown / missing-terminal-sentinel failure mode
    // is dominated by agents that DID sign off but not in the exact
    // [[TASK_DONE]] shape. A malformed-but-unambiguous sign-off on its own line
    // must be treated as an authoritative sentinel, not dropped to heuristic.

    [Theory]
    [InlineData("[TASK_DONE]")]                       // single bracket pair (claude near-miss)
    [InlineData("TASK_DONE")]                          // bare whole-line token (codex near-miss)
    [InlineData("**[[TASK_DONE]]**")]                  // markdown bold decoration
    [InlineData("`[[TASK_DONE]]`")]                    // inline-code decoration
    [InlineData("> [[TASK_DONE]]")]                    // blockquote decoration
    [InlineData("- [[TASK_DONE]]")]                    // list-bullet decoration
    [InlineData("[[ TASK DONE ]]")]                    // spaced separators
    public void TolerantSentinel_DoneNearMiss_MatchesAuthoritatively(string sentinelLine)
    {
        var lines = Lines("I implemented the change and verified it.", sentinelLine);
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "completed", durationSeconds: 30.0);
        Assert.Equal(AgentOutcomeKind.Done, outcome.Kind);
        Assert.True(outcome.MatchedSentinel);
        Assert.Equal("DONE", outcome.SentinelKeyword);
        Assert.Equal(RunIssueKind.None, outcome.IssueKind);
    }

    [Fact]
    public void TolerantSentinel_BlockedSingleBracket_CapturesReasonAuthoritatively()
    {
        var lines = Lines("I could not find the credentials file.", "[TASK_BLOCKED: missing credentials]");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "completed", durationSeconds: 12.0);
        Assert.Equal(AgentOutcomeKind.Blocked, outcome.Kind);
        Assert.True(outcome.MatchedSentinel);
        Assert.Equal("missing credentials", outcome.Reason);
    }

    [Fact]
    public void TolerantSentinel_BareNeedsInput_MatchesAuthoritatively()
    {
        var lines = Lines("Which database should I target?", "TASK_NEEDS_INPUT: pick a target db");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "completed", durationSeconds: 8.0);
        Assert.Equal(AgentOutcomeKind.NeedsInput, outcome.Kind);
        Assert.True(outcome.MatchedSentinel);
    }

    [Fact]
    public void TolerantSentinel_DoubleBracketStillWinsAndIsPreferred()
    {
        // A canonical [[TASK_DONE]] anywhere must still match exactly as before -
        // the tolerant path is a fallback, never a regression of the strict one.
        var lines = Lines("Some prose.", "[[TASK_DONE]]");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "completed", durationSeconds: 20.0);
        Assert.Equal(AgentOutcomeKind.Done, outcome.Kind);
        Assert.True(outcome.MatchedSentinel);
    }

    // ---- False-positive guard: tolerant matching is line-anchored ----------
    // Prose that merely mentions the token mid-sentence must NOT be read as a
    // sentinel. This is the reason the tolerant regex anchors the whole token
    // to one line: loosening it to a substring match would mis-classify the
    // contract being quoted or discussed.

    [Theory]
    [InlineData("The task is done so far, but I want to keep going.")]
    [InlineData("I will emit TASK_DONE on its own line when the work is finished.")]
    [InlineData("Next I should mark TASK_DONE in the tracker once tests are green.")]
    public void TolerantSentinel_TokenMentionedMidProse_DoesNotMatchSentinel(string prose)
    {
        var lines = Lines(prose);
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "completed", durationSeconds: 18.0);
        Assert.False(outcome.MatchedSentinel);
    }

    // ---- Heuristic shape coverage for real claude/codex sign-offs ----------
    // Real "done" replies rarely match the original verb list verbatim. These
    // pin the widened shapes so a clear completion summary classifies as Done
    // (MissingTerminalSentinel - non-terminal) rather than Unknown
    // (classifier-unknown).

    [Theory]
    [InlineData("## Summary of changes\n\nI refactored the runner policy and split the read-only containment out.")]
    [InlineData("Here's what I did: renamed the helper, migrated the call sites, and updated the docs.")]
    [InlineData("I've refactored the analyzer and all tests pass.")]
    [InlineData("Done - the build succeeds and the new spec is green.")]
    [InlineData("Validated the change against the contract test; everything is green.")]
    [InlineData("All set. Removed the dead branch and documented the new flow.")]
    public void Heuristic_RealDoneShapes_ClassifyAsDone(string reply)
    {
        var lines = Lines(reply);
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "completed", durationSeconds: 45.0);
        Assert.Equal(AgentOutcomeKind.Done, outcome.Kind);
        Assert.False(outcome.MatchedSentinel);
        Assert.Equal(RunIssueKind.MissingTerminalSentinel, outcome.IssueKind);
    }

    [Fact]
    public void Heuristic_LeadingCheckmark_ClassifiesAsDone()
    {
        var lines = Lines("✅ Implementation finished and the suite is green.");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "completed", durationSeconds: 40.0);
        Assert.Equal(AgentOutcomeKind.Done, outcome.Kind);
    }

    [Theory]
    [InlineData("I could not complete this - the upstream API is down.")]
    [InlineData("I'm blocked by a missing migration that only the user can run.")]
    [InlineData("Unable to proceed: no permission to write outside the job folder.")]
    public void Heuristic_RealBlockedShapes_ClassifyAsBlocked(string reply)
    {
        var lines = Lines(reply);
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "completed", durationSeconds: 25.0);
        Assert.Equal(AgentOutcomeKind.Blocked, outcome.Kind);
        Assert.False(outcome.MatchedSentinel);
    }

    [Theory]
    [InlineData("Which approach would you like me to take?")]
    [InlineData("Let me know whether to target staging or prod before I continue.")]
    public void Heuristic_RealNeedsInputShapes_ClassifyAsNeedsInput(string reply)
    {
        var lines = Lines(reply);
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "completed", durationSeconds: 14.0);
        Assert.Equal(AgentOutcomeKind.NeedsInput, outcome.Kind);
    }

    [Theory]
    [InlineData("Prompt too long")]
    [InlineData("Error: prompt is too long: 250000 tokens > 200000 maximum")]
    [InlineData("This model's maximum context length is 200000 tokens")]
    [InlineData("context_length_exceeded")]
    [InlineData("HTTP 413 Payload Too Large")]
    public void ContextOverflow_OnFailedRun_TypesAsContextOverflow(string reply)
    {
        // The exact failure behind the endless-reissue loop: a "Prompt too
        // long" failure was being classified as classifier-unknown and
        // re-issued forever. It must now be typed so policy routes it to
        // human review instead.
        var lines = Lines(reply);
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "failed", durationSeconds: 8.0);
        Assert.Equal(RunIssueKind.ContextOverflow, outcome.IssueKind);
    }

    [Fact]
    public void ContextOverflow_PhraseOnSuccessfulRun_IsNotTyped()
    {
        // An agent quoting "prompt too long" mid-success must not be hijacked;
        // the detection is gated on a failed run.
        var lines = Lines("I shortened the prompt because the prompt was too long for the test fixture.", "[[TASK_DONE]]");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "completed", durationSeconds: 20.0);
        Assert.NotEqual(RunIssueKind.ContextOverflow, outcome.IssueKind);
        Assert.Equal(AgentOutcomeKind.Done, outcome.Kind);
    }

    [Theory]
    // The exact codex ChatGPT-account signature (AGT-1928/1929/1930/1936):
    // codex-cli 0.143 rejects -m gpt-5-codex with a 400 invalid_request.
    [InlineData("● Turn failed: {\"type\":\"error\",\"status\":400,\"error\":{\"type\":\"invalid_request_error\",\"message\":\"The 'gpt-5-codex' model is not supported when using Codex with a ChatGPT account.\"}}")]
    [InlineData("Error: model_not_found")]
    [InlineData("The model `gpt-9` does not exist or you do not have access to it.")]
    [InlineData("unsupported model: foo-bar")]
    public void ModelInvalid_OnFailedRun_TypesAsModelInvalid(string reply)
    {
        // A wrong/unsupported model must type as model-invalid (non-retryable)
        // instead of the orchestrator-inconclusive catch-all, so the escalation
        // reason tells a human to change the model (AGT-1941).
        var lines = Lines(reply);
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "failed", durationSeconds: 4.9, exitCode: 1);
        Assert.Equal(RunIssueKind.ModelInvalid, outcome.IssueKind);
    }

    [Fact]
    public void ModelInvalid_PhraseOnSuccessfulRun_IsNotTyped()
    {
        // An agent discussing a model-support error mid-success must not be
        // hijacked; the detection is gated on a failed run.
        var lines = Lines("I noted that the old model is not supported, then switched the config.", "[[TASK_DONE]]");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "completed", durationSeconds: 20.0);
        Assert.NotEqual(RunIssueKind.ModelInvalid, outcome.IssueKind);
        Assert.Equal(AgentOutcomeKind.Done, outcome.Kind);
    }

    [Theory]
    // The exact AGT-1918/1919/1920 signature: claude-sonnet-5 five-hour
    // session-limit rejection on 2026-07-07.
    [InlineData("You've hit your session limit · resets 8:10pm (Europe/Berlin)")]
    [InlineData("● Rate limit · five-hour · rejected · reset in 3,6 h  [window=five_hour status=rejected resetsAt=1783447800 overage=rejected usingOverage=false]")]
    [InlineData("Error: rate_limit_exceeded")]
    [InlineData("You've reached your usage limit for this model.")]
    public void QuotaExhausted_OnFailedRun_TypesAsQuotaExhausted(string reply)
    {
        // A usage/session/rate-limit exhaustion must type as quota-exhausted
        // (transient) instead of the orchestrator-inconclusive catch-all so the
        // escalation reason is honest and re-queue-after-reset is the clear next
        // step (AGT-1918/1919/1920).
        var lines = Lines(reply);
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "failed", durationSeconds: 5.1, exitCode: 1);
        Assert.Equal(RunIssueKind.QuotaExhausted, outcome.IssueKind);
    }

    [Fact]
    public void QuotaExhausted_BenignRateLimitTelemetryOnSuccess_IsNotTyped()
    {
        // Claude prints a benign `Rate limit ... allowed` telemetry marker on
        // healthy runs. It must NOT be read as exhaustion: detection matches
        // only the rejected/exhausted shapes and is gated on a failed run.
        var lines = Lines(
            "● Rate limit · five-hour · allowed  [window=five_hour status=allowed resetsAt=1783447800 overage=none usingOverage=false]",
            "[[TASK_DONE]]");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "completed", durationSeconds: 25.0);
        Assert.NotEqual(RunIssueKind.QuotaExhausted, outcome.IssueKind);
        Assert.Equal(AgentOutcomeKind.Done, outcome.Kind);
    }

    [Theory]
    // Host file-lock family (MSB302x copy-lock): a build output was momentarily
    // locked by a lingering process. The lock releases on its own, so this is a
    // transient environmental fault, not a code failure (AGT-1944).
    [InlineData("error MSB3027: Could not copy \"obj\\Api.dll\" to \"bin\\Api.dll\". Exceeded retry count of 10. Failed.")]
    [InlineData("error MSB3021: Unable to copy file \"a.dll\" to \"b.dll\". The process cannot access the file 'b.dll' because it is being used by another process.")]
    [InlineData("The process cannot access the file because it is being used by another process.")]
    // Network glitches: DNS failure, reset/timed-out sockets, transient gateways.
    [InlineData("fatal: unable to access 'https://github.com/x.git/': Could not resolve host: github.com")]
    [InlineData("Error: connect ECONNRESET 140.82.113.3:443")]
    [InlineData("dial tcp: lookup api.example.com: Temporary failure in name resolution")]
    [InlineData("HTTP 503 Service Unavailable")]
    // Torn-down-/tmp family (AGT-2750): a PrivateTmp=true unit restart deletes
    // the mount out from under a still-running detached worker (KillMode=process
    // deliberately keeps it alive). MSBuild's node pipe and NuGet's mkdtemp mutex
    // fail against the deleted mount; this is host infrastructure, not a code
    // failure in the change under test.
    [InlineData("MSB1025: Startup of MSBuild's node communication pipe failed: SocketException (99): Cannot assign requested address")]
    [InlineData("System.IO.IOException: mkdtemp(\"/tmp/.dotnet.AbC123\") == nullptr; errno == ENOENT")]
    public void EnvironmentalTransient_OnFailedRun_TypesAsEnvironmentalTransient(string reply)
    {
        // A transient host file lock / network glitch must type as
        // environmental-transient so the runner retries it with backoff instead
        // of escalating it as a code failure.
        var lines = Lines("Running the post-build test gate...", reply);
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "failed", durationSeconds: 42.0, exitCode: 1);
        Assert.Equal(RunIssueKind.EnvironmentalTransient, outcome.IssueKind);
    }

    [Fact]
    public void EnvironmentalTransient_PhraseOnSuccessfulRun_IsNotTyped()
    {
        // An agent that merely mentions a lock/network phrase in a healthy turn
        // must not be hijacked; detection is gated on a failed run.
        var lines = Lines(
            "I retried the copy after the file was being used by another process, then it succeeded.",
            "[[TASK_DONE]]");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "completed", durationSeconds: 30.0);
        Assert.NotEqual(RunIssueKind.EnvironmentalTransient, outcome.IssueKind);
        Assert.Equal(AgentOutcomeKind.Done, outcome.Kind);
    }
}
