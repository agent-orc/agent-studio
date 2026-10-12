
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the deterministic post-DONE completion gate: the scan that reads a
/// run's own close-out (status Open Items / Notes / Result line plus the log
/// tail) for unfinished-work evidence before auto-review may accept the task.
/// Covers the expanded build/compile/test-failure vocabulary and the
/// "claims success but build failed" contradiction rule that closed the
/// ASS-764 / ASS-766 silent-completion gap.
/// </summary>
public class CompletionGateTests
{
    private const int MaxReissues = 3;

    [Fact]
    public void EvaluateStructured_Agt2149DoneRun_DoesNotInterpretBlockedProseAsOpenWork()
    {
        var log = string.Join('\n',
            "Status result is Blocked.",
            "GitHub organization owner must approve deploy keys to unblock real host verification.",
            "Host access to agent-runner-01 required to confirm push identity.",
            "No repository-side change can proceed further without external approval.",
            "[[TASK_DONE]]",
            "[taskboard] codex CLI exited: status=completed, exitCode=0, duration=42s");
        var record = CompletionAcceptanceRecord.Capture(
            "Implement runner push identity.", log, exitCode: 0,
            runStatusCompleted: true, hasResultsArtifacts: false);

        var decision = CompletionGate.EvaluateStructured(record, priorReissues: 2, maxReissues: 3);

        Assert.Equal(CompletionGate.CompletionGateAction.Pass, decision.Action);
        Assert.Empty(record.Blockers);
        Assert.True(record.Lifecycle.TurnComplete);
        Assert.True(record.Lifecycle.ImplementationComplete);
        Assert.False(record.Lifecycle.TaskAccepted);
        Assert.True(record.Lifecycle.DeploymentPushPending);
        Assert.Contains(record.Requirements, r => r.Source.StartsWith("prompt.md", StringComparison.Ordinal));
        Assert.All(record.Evidence, e => Assert.False(string.IsNullOrWhiteSpace(e.Source)));
    }

    [Fact]
    public void EvaluateStructured_ExplicitBlocker_PersistsSourceAndReasonAndEscalates()
    {
        var log = "[[TASK_BLOCKED:deploy key approval required]]\n" +
                  "[taskboard] codex CLI exited: status=completed, exitCode=0, duration=42s";
        var record = CompletionAcceptanceRecord.Capture(
            "Verify on the runner host.", log, exitCode: 0,
            runStatusCompleted: true, hasResultsArtifacts: false);

        var decision = CompletionGate.EvaluateStructured(record, priorReissues: 0, maxReissues: 3);

        Assert.Equal(CompletionGate.CompletionGateAction.Escalate, decision.Action);
        var blocker = Assert.Single(record.Blockers);
        Assert.Equal("logs/cli-output.log", blocker.Source);
        Assert.Equal("deploy key approval required", blocker.Reason);
        Assert.Contains("source: logs/cli-output.log", Assert.Single(decision.Findings));
    }

    [Fact]
    public void ExtractFindings_CleanCloseOut_NoFindings()
    {
        var status = string.Join('\n',
            "## Summary",
            "Implemented the feature and verified the build.",
            "",
            "Result: Success",
            "",
            "## Open Items",
            "None");

        var findings = CompletionGate.ExtractFindings(status, recentLog: "[12:00] build succeeded");

        Assert.Empty(findings);
    }

    [Fact]
    public void ExtractFindings_OpenItemsCheckbox_IsReported()
    {
        var status = string.Join('\n',
            "## Open Items",
            "- [ ] Wire the new route into the shell",
            "- [x] Already done thing");

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.Contains(findings, f => f.Contains("Wire the new route", System.StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    // ASS-797: a non-actionable "None" follow-up checkbox the agent can never
    // close. Each variant must NOT become a finding, or the task ping-pongs back
    // to 2-ready forever / escalates grundlos.
    [InlineData("- [ ] None. Changes left in working tree per managed-run guidelines.")]
    [InlineData("- None. Changes left in working tree per managed-run guidelines.")]
    [InlineData("- [ ] None.")]
    [InlineData("- [ ] N/A")]
    [InlineData("- [ ] Nothing to do.")]
    [InlineData("- [ ] No follow-ups required.")]
    [InlineData("- [ ] No open items.")]
    public void ExtractFindings_PhantomNoneOpenItem_IsNotReported(string openItemLine)
    {
        var status = string.Join('\n', "## Open Items", openItemLine);

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.Empty(findings);
    }

    [Fact]
    public void ExtractFindings_PhantomNoneOpenItem_DoesNotTriggerReissueOrEscalate()
    {
        // The whole point of AC3: a phantom "None" item must pass the gate at
        // both ends of the reissue budget, not reissue (budget left) or escalate
        // (budget spent).
        var status = "## Summary\nDone.\n\nResult: Success\n\n## Open Items\n- [ ] None. Changes left in working tree per managed-run guidelines.\n";

        var fresh = CompletionGate.Evaluate(status, recentLog: null, priorReissues: 0, maxReissues: MaxReissues);
        var spent = CompletionGate.Evaluate(status, recentLog: null, priorReissues: MaxReissues, maxReissues: MaxReissues);

        Assert.Equal(CompletionGate.CompletionGateAction.Pass, fresh.Action);
        Assert.Equal(CompletionGate.CompletionGateAction.Pass, spent.Action);
    }

    [Fact]
    public void ExtractFindings_RealItemStartingWithNone_StillReported()
    {
        // Guard the guard: a genuine open item that merely opens with "None"
        // must NOT be swallowed by the phantom-None suppression.
        var status = "## Open Items\n- [ ] None of the routes are wired into the shell yet.";

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.Contains(findings, f => f.Contains("routes are wired", System.StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    // AGT-1986: the run honestly annotated an item as pre-existing / out-of-scope
    // (not work this change owns). The gate must NOT count these as unfinished
    // work, or it escalates a fully-merged run over a failure it did not cause.
    [InlineData("- [ ] Flaky auth test fails intermittently (pre-existing, not caused by this change).")]
    [InlineData("- [ ] [pre-existing] Lint warnings in the legacy module.")]
    [InlineData("- [ ] Broken snapshot in the shared package (out of scope for this task).")]
    [InlineData("- [ ] Pre-existing: the e2e suite is red on main.")]
    [InlineData("- [ ] The build warning is not introduced by these changes.")]
    [InlineData("- The vendored bundle mismatch is not caused by this change.")]
    public void ExtractFindings_PreExistingOrOutOfScopeItem_IsNotReported(string openItemLine)
    {
        var status = string.Join('\n', "## Open Items", openItemLine);

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.Empty(findings);
    }

    [Fact]
    public void ExtractFindings_PreExistingEvidenceLine_IsNotReported()
    {
        // A build/test-failure evidence line the run disclaimed as pre-existing
        // must not surface as a finding (it would otherwise match the
        // build-failure vocabulary and escalate the run).
        var status = "## Notes\nThe integration tests fail, but that is pre-existing and not caused by this change.";

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.Empty(findings);
    }

    [Fact]
    public void ExtractFindings_GenuinePreExistingWork_StillReported()
    {
        // Guard the guard: a genuine actionable item that merely uses the
        // adjective "pre-existing" (with no disclaimer marker) must still be
        // reported - the run chose to note it as work to do.
        var status = "## Open Items\n- [ ] Fix the pre-existing null-check bug in the parser as part of this task.";

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.Contains(findings, f => f.Contains("null-check bug", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractFindings_PreExistingItem_DoesNotTriggerReissueOrEscalate()
    {
        // AGT-1986 end to end: a pre-existing-marked item must pass the gate at
        // both ends of the reissue budget so a fully-merged run is not escalated.
        var status = "## Summary\nDone.\n\nResult: Success\n\n## Open Items\n- [ ] Flaky auth test (pre-existing, not caused by this change).\n";

        var fresh = CompletionGate.Evaluate(status, recentLog: null, priorReissues: 0, maxReissues: MaxReissues);
        var spent = CompletionGate.Evaluate(status, recentLog: null, priorReissues: MaxReissues, maxReissues: MaxReissues);

        Assert.Equal(CompletionGate.CompletionGateAction.Pass, fresh.Action);
        Assert.Equal(CompletionGate.CompletionGateAction.Pass, spent.Action);
    }

    [Theory]
    // The reissue loop's own trigger: a commit/push-delegation follow-up the
    // agent can never close because the platform owns the commit boundary
    // (docs/operations/git/commit-push-doctrine.md). Each variant must NOT become a finding,
    // or the managed run ping-pongs back to 2-ready forever / escalates grundlos.
    [InlineData("- [ ] Working tree changes awaiting managed-run commit/push (platform owns merge).")]
    [InlineData("- [ ] Changes left in the working tree for the platform to commit.")]
    [InlineData("- [ ] Commit/push handled by the managed run.")]
    [InlineData("- [ ] Changes committed by the platform after this run.")]
    [InlineData("- [ ] Awaiting the managed run to push the work.")]
    [InlineData("- [ ] Final commit per managed-run guidelines.")]
    [InlineData("- Changes awaiting commit/push.")]
    public void ExtractFindings_PlatformOwnedCommitItem_IsNotReported(string openItemLine)
    {
        var status = string.Join('\n', "## Open Items", openItemLine);

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.Empty(findings);
    }

    [Fact]
    public void ExtractFindings_PlatformOwnedCommitItem_DoesNotTriggerReissueOrEscalate()
    {
        // The exact reissue trigger this fix targets: a commit-delegation item
        // must pass the gate at both ends of the reissue budget.
        var status = "## Summary\nDone.\n\nResult: Success\n\n## Open Items\n- [ ] Working tree changes awaiting managed-run commit/push (platform owns merge).\n";

        var fresh = CompletionGate.Evaluate(status, recentLog: null, priorReissues: 0, maxReissues: MaxReissues);
        var spent = CompletionGate.Evaluate(status, recentLog: null, priorReissues: MaxReissues, maxReissues: MaxReissues);

        Assert.Equal(CompletionGate.CompletionGateAction.Pass, fresh.Action);
        Assert.Equal(CompletionGate.CompletionGateAction.Pass, spent.Action);
    }

    [Theory]
    // Guard the guard: genuine work that happens to mention commit/push/merge
    // must still be reported. The delegation must be explicit to suppress.
    [InlineData("- [ ] Push the migration to the shared config repo.")]
    [InlineData("- [ ] Commit the generated client SDK before release.")]
    [InlineData("- [ ] Merge the feature branch into develop once review passes.")]
    public void ExtractFindings_RealCommitWork_StillReported(string openItemLine)
    {
        var status = string.Join('\n', "## Open Items", openItemLine);

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.NotEmpty(findings);
    }

    [Fact]
    public void ExtractFindings_PartialResult_IsReported()
    {
        var status = "Result: Partial";

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.Contains(findings, f => f.Contains("Partial", System.StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("The build failed during compilation.")]
    [InlineData("Note: build is broken on main.")]
    [InlineData("The component does not compile yet.")]
    [InlineData("It won't compile because of a missing import.")]
    [InlineData("Saw compile errors in the runner.")]
    [InlineData("error TS2304: cannot find name 'Foo'.")]
    [InlineData("error CS0103: the name does not exist.")]
    [InlineData("error NG8001: unknown element.")]
    [InlineData("There are typescript errors remaining.")]
    [InlineData("npm ERR! build step exited with 1.")]
    [InlineData("Application bundle generation failed.")]
    [InlineData("Several tests failed in the suite.")]
    public void ExtractFindings_BuildErrorVocabulary_InNotes_IsReported(string noteLine)
    {
        var status = string.Join('\n', "## Notes", noteLine);

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.NotEmpty(findings);
    }

    [Fact]
    public void ExtractFindings_BuildErrorInLogTail_IsReported_WhenStatusOmitsIt()
    {
        var status = "## Summary\nLooks good.";
        var log = string.Join('\n',
            "[12:00] running ng build",
            "[12:01] error TS2552: cannot find name 'foo'",
            "[12:02] [[TASK_DONE]]");

        var findings = CompletionGate.ExtractFindings(status, log);

        Assert.NotEmpty(findings);
    }

    [Fact]
    public void ExtractFindings_SuccessResult_WithBuildError_RaisesContradiction()
    {
        var status = string.Join('\n',
            "Result: Success",
            "## Notes",
            "Final build failed with error CS0246.");

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.Contains(findings, f =>
            f.Contains("Status result is Success", System.StringComparison.OrdinalIgnoreCase) &&
            f.Contains("build/test failure evidence", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractFindings_SuccessResult_NoBuildError_NoContradiction()
    {
        var status = string.Join('\n',
            "Result: Success",
            "## Notes",
            "Everything verified, build is green.");

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.DoesNotContain(findings, f =>
            f.Contains("build/test failure evidence", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractFindings_SuccessResult_ExplicitFalsePositiveBuildFailure_IsNotReported()
    {
        // AGT-2148 reissue regression: an early --no-restore probe failed only
        // because project.assets.json did not exist yet. The final close-out
        // explicitly disclaimed that signal as a false positive after a clean
        // restored build, so it is historical context rather than open work.
        var status = string.Join('\n',
            "Result: Success",
            "## What Was Done",
            "- Identified earlier \"Build FAILED\" as a false positive: `dotnet build --no-restore` ran before `project.assets.json` was created.",
            "- Rebuilt with restore and all tests passed.",
            "## Open Items",
            "None.");

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.Empty(findings);
    }

    [Fact]
    public void ExtractFindings_UnresolvedBuildFailure_StillReported()
    {
        // Guard the guard: "earlier" alone does not disclaim a failure. Only an
        // explicit false-positive diagnosis may suppress build-failure text.
        var status = "Result: Success\n## Notes\nThe earlier build FAILED and has not been rerun.";

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.Contains(findings, f =>
            f.Contains("build FAILED", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractFindings_UnrelatedFalsePositivePhrase_DoesNotHidePendingWork()
    {
        var status = "Result: Success\n## Notes\nThe false-positive detector wiring is still pending.";

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.Contains(findings, f =>
            f.Contains("still pending", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractFindings_EchoedPipelineExecutionFinding_IsNotReported()
    {
        // AGT-2148 reissue regression: an rg/Get-Content result from the
        // pipeline history repeated the gate's own old "Build FAILED" reason.
        // It is artifact content, not fresh build/test output from this run.
        var status = "Result: Success\n## Open Items\nNone.";
        var log = string.Join('\n',
            "[20:48:40.074] [stderr] C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard\\tasks\\002\\AGT-2148\\pipeline-execution.json:41:     \"reason\": \"2 open item(s): [19:26:29.151] [stderr] Build FAILED.; Status result is Success but build/test failure evidence was found\"",
            "[20:48:41.000] [stdout] [[TASK_DONE]]");

        var findings = CompletionGate.ExtractFindings(status, log);

        Assert.Empty(findings);
    }

    [Fact]
    public void ExtractFindings_SuccessfulFixedProblemAndNegatedSourceEcho_AreNotReported()
    {
        // AGT-2148 second reissue regression: the successful status described
        // the stale failure that was fixed, while rg echoed source containing
        // the phrase "no unfinished evidence". Neither is an open item.
        var status = string.Join('\n',
            "Result: Success",
            "## Overview",
            "- Problem: Completion-gate was incorrectly re-opening tasks by retaining stale \"Build FAILED\" evidence from early pre-restore attempts as unresolved open items.",
            "- Solution: Later restored verification passed.",
            "## What Was Done",
            "- Verified: targeted suite 67/67 tests passed.",
            "## Open Items",
            "None.");
        var log = string.Join('\n',
            "[20:57:08.894] [stderr] .\\frontend\\e2e\\task-detail\\pipeline-orchestrator-review-distinct.spec.ts:121: verdictSummary: 'Post-core completeness check: no unfinished evidence in the close-out.',",
            "[21:04:52.418] [stdout] [[TASK_DONE]]",
            "[21:04:52.980] [system] [taskboard] codex CLI exited: status=completed, exitCode=0, duration=492.0s");

        var findings = CompletionGate.ExtractFindings(status, log);
        var decision = CompletionGate.Evaluate(status, log, priorReissues: 3, maxReissues: 3);

        Assert.Empty(findings);
        Assert.Equal(CompletionGate.CompletionGateAction.Pass, decision.Action);
    }

    [Fact]
    public void ExtractFindings_GreppedSourceLineWithKeyword_InLogTail_IsNotReported()
    {
        // ASS-794 regression: the previous run grepped its own source and echoed
        // a line whose only "incomplete" token is a PARAMETER named `pending`.
        // The gate must not read an identifier in printed code as unfinished work
        // and reissue the task forever.
        var status = "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n";
        var log = string.Join('\n',
            "[17:15:20.243] [stdout] running grep",
            "[17:15:20.243] [stdout]   814:                HandleAcceptAsDone(workspace, entry, pending, prompt, response, verdict);",
            "[17:15:21.000] [stdout] [[TASK_DONE]]");

        var findings = CompletionGate.ExtractFindings(status, log);

        Assert.Empty(findings);
    }

    [Fact]
    public void ExtractFindings_SteerPendingArtifactInSuccessfulLog_IsNotReported()
    {
        // AGT-2206 operator-sweep regression: the UI iteration close-out named
        // its completed handoff artifact `steer-pending.json`. The generic
        // incomplete-work vocabulary read the filename segment as the status
        // word "pending", then reissued and eventually escalated the completed
        // run. Preserve standalone pending prose while ignoring identifiers.
        var status = "Result: Success\n## Open Items\nNone.";
        var log = string.Join('\n',
            "[13:51:13.274] [stderr] - Review pause in `5-human-review` with versioned `steer-pending.json`; an additional iteration at the cap escalates to `5e-escalated`.",
            "[13:51:13.419] [stdout] - Review pause in `5-human-review` with versioned `steer-pending.json`; an additional iteration at the cap escalates to `5e-escalated`.",
            "[13:51:13.450] [system] ui_pipeline_review_pending project=agent-taskboard job=AGT-2206 pipeline=ui-iteration-task-pipeline iteration=2/4 artifacts=1 marker=/tasks/AGT-2206/steer-pending.json",
            "[13:51:13.500] [stdout] [[TASK_DONE]]",
            "[13:51:13.600] [system] [taskboard] codex CLI exited: status=completed, exitCode=0, duration=492.0s");

        var findings = CompletionGate.ExtractFindings(status, log);
        var decision = CompletionGate.Evaluate(
            status, log, priorReissues: 2, maxReissues: 2);

        Assert.Empty(findings);
        Assert.Equal(CompletionGate.CompletionGateAction.Pass, decision.Action);
    }

    [Fact]
    public void ExtractFindings_RealPendingProse_StillReported()
    {
        // Guard the guard: a genuine status sentence using "pending" as a status
        // word (no line-number/code-echo shape) must still be caught.
        var status = string.Join('\n',
            "## Notes",
            "The route wiring is still pending.");

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.NotEmpty(findings);
    }

    [Fact]
    public void ExtractFindings_GreppedBuildErrorString_InLogTail_IsNotReported()
    {
        // A grepped source line that merely contains the literal "build failed"
        // inside code (e.g. an error-message string) is an echo, not the run's
        // own close-out, so it must not trip the build-error vocabulary.
        var status = "## Summary\nDone.\n\nResult: Success\n";
        var log = string.Join('\n',
            "[12:00] grep build",
            "[12:00]   42:    throw new Exception(\"build failed unexpectedly\");",
            "[12:01] [[TASK_DONE]]");

        var findings = CompletionGate.ExtractFindings(status, log);

        Assert.Empty(findings);
    }

    // ── Per-run evidence scoping (MKT-8 gate-latch regression) ────────────

    [Fact]
    public void ScopeToLatestRunWindow_CleanFinalRun_DropsOlderRunLines()
    {
        var log = string.Join('\n',
            "[21:35:22.996] [system] [taskboard] Started codex CLI (PID 17912), model=gpt-5.6-sol",
            "[21:42:38.221] [stderr] ERROR codex_core::tools::router: error=apply_patch verification failed: invalid hunk at line 2",
            "[21:44:48.607] [system] [taskboard] codex CLI exited: status=completed, exitCode=0, duration=565,6s",
            "[21:48:46.506] [system] [taskboard] Started codex CLI (PID 63216), model=gpt-5.6-sol",
            "[21:52:59.000] [stdout] Verified the target file contains the full intended content.",
            "[21:53:06.256] [system] [taskboard] codex CLI exited: status=completed, exitCode=0, duration=259,8s");

        var window = CompletionGate.ScopeToLatestRunWindow(log);

        Assert.DoesNotContain("apply_patch", window, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PID 63216", window);
        Assert.Contains("Verified the target file", window);
    }

    [Fact]
    public void ScopeToLatestRunWindow_DirtyFinalRun_KeepsWholeLog()
    {
        // A failed final run must NOT hide older evidence: scoping only applies
        // when the latest exit is a clean terminal.
        var log = string.Join('\n',
            "[12:00] [system] [taskboard] Started codex CLI (PID 1)",
            "[12:01] [stderr] error CS0103: the name does not exist.",
            "[12:02] [system] [taskboard] codex CLI exited: status=completed, exitCode=0, duration=60s",
            "[12:10] [system] [taskboard] Started codex CLI (PID 2)",
            "[12:11] [system] [taskboard] codex CLI exited: status=failed, exitCode=1, duration=10s");

        Assert.Equal(log, CompletionGate.ScopeToLatestRunWindow(log));
    }

    [Fact]
    public void ScopeToLatestRunWindow_NoExitMarker_KeepsWholeLog()
    {
        var log = "[12:00] [stdout] doing work\n[12:01] [stdout] [[TASK_DONE]]";

        Assert.Equal(log, CompletionGate.ScopeToLatestRunWindow(log));
    }

    [Fact]
    public void ExtractFindings_OldRunApplyPatchError_ClearedByCleanFinalRun()
    {
        // MKT-8 round 1: run 1's apply_patch failure sat in the accumulated log
        // tail; the reissued run terminated clean and verified the fix, but the
        // gate re-read the old line as fresh evidence. Per-run scoping must
        // clear evidence that a clean terminal run superseded.
        var status = "## Summary\nDone.\n\nResult: Success\n\n## Open Items\nNone\n";
        var log = string.Join('\n',
            "[21:35:22.996] [system] [taskboard] Started codex CLI (PID 17912)",
            "[21:42:38.221] [stderr] ERROR codex_core::tools::router: error=apply_patch verification failed: invalid hunk at line 2",
            "[21:44:48.607] [system] [taskboard] codex CLI exited: status=completed, exitCode=0, duration=565,6s",
            "[21:48:46.506] [system] [taskboard] Started codex CLI (PID 63216)",
            "[21:52:59.000] [stdout] [[TASK_DONE]]",
            "[21:53:06.256] [system] [taskboard] codex CLI exited: status=completed, exitCode=0, duration=259,8s");

        var findings = CompletionGate.ExtractFindings(status, log);

        Assert.Empty(findings);
    }

    [Fact]
    public void ExtractFindings_ApplyPatchErrorInLatestRun_StillReported()
    {
        // Guard the guard: a failure INSIDE the latest run's own window remains
        // evidence even though the run claims a clean exit.
        var status = "## Summary\nDone.\n\nResult: Success\n";
        var log = string.Join('\n',
            "[21:48:46.506] [system] [taskboard] Started codex CLI (PID 63216)",
            "[21:50:00.000] [stderr] ERROR codex_core::tools::router: error=apply_patch verification failed: invalid hunk at line 2",
            "[21:53:06.256] [system] [taskboard] codex CLI exited: status=completed, exitCode=0, duration=259,8s");

        var findings = CompletionGate.ExtractFindings(status, log);

        Assert.NotEmpty(findings);
    }

    // ── Gate-verdict echo suppression (MKT-8 self-sustaining latch) ───────

    [Theory]
    // The gate's own verdict, echoed back at it: the supervisor escalate line,
    // the reissue follow-up, the agent's narration of / investigation into the
    // prior verdict. None of these is the run's own claim of open work.
    [InlineData("[21:53:25.191] [supervisor] [escalate] Auto-review completion gate could not clear unfinished-work evidence. Reason: [completion-gate-unresolved] Completion gate found unfinished-work evidence after 2 prior orchestrator reissue(s); user attention required.")]
    [InlineData("The Orchestrator Completion-Gate found unfinished work in the previous run's own result/status evidence.")]
    [InlineData("Der Completion-Gate hat eine einzelne, später behobene `apply_patch`-Fehlermeldung aus dem alten CLI-Log als „unfinished“ interpretiert.")]
    [InlineData("[21:46:24.366] [stdout] ● Run \"C:\\WINDOWS\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -Command \"rg -n -i \\\"unfinished|incomplete|pending\\\" .\"")]
    public void ExtractFindings_GateVerdictEcho_InLog_IsNotReported(string echoLine)
    {
        var status = "Result: Success\n## Open Items\nNone.";
        var log = string.Join('\n',
            echoLine,
            "[21:53:00.000] [stdout] [[TASK_DONE]]");

        var findings = CompletionGate.ExtractFindings(status, log);

        Assert.Empty(findings);
    }

    [Fact]
    public void ExtractFindings_VerdictNarrationInStatusNotes_IsNotReported()
    {
        // MKT-8's literal close-out: the Notes honestly narrate that the gate
        // flagged an already-fixed apply_patch error as "unfinished" and that
        // the agent verified the fix. That narration must not re-feed the gate.
        var status = string.Join('\n',
            "# Status",
            "",
            "- Result: Success",
            "",
            "## Open Items",
            "- Release gate: remains locked pending explicit approval of reproducible quickstart and onboarding card acceptance.",
            "",
            "## Notes",
            "- Multiple session recoveries due to Codex rollout state loss during run; final pass completed with full validation.",
            "- Auto-review gate flagged prior apply_patch error from first run as \"unfinished\"; agent verified target file contained all intended content correctly.");

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.Empty(findings);
    }

    [Fact]
    public void Evaluate_Mkt8ShapedCloseOut_PassesAtBudgetEnd()
    {
        // End to end for the MKT-8 escalation: budget spent, clean final run,
        // status narrating the prior verdict plus an approval-gated deliverable
        // -> the gate must PASS instead of escalating to 5e.
        var status = string.Join('\n',
            "- Result: Success",
            "",
            "## Open Items",
            "- Release gate: remains locked pending explicit approval of reproducible quickstart and onboarding card acceptance.",
            "",
            "## Notes",
            "- Auto-review gate flagged prior apply_patch error from first run as \"unfinished\"; agent verified target file contained all intended content correctly.");
        var log = string.Join('\n',
            "[21:35:22.996] [system] [taskboard] Started codex CLI (PID 17912)",
            "[21:42:38.221] [stderr] ERROR codex_core::tools::router: error=apply_patch verification failed: invalid hunk at line 2",
            "[21:44:48.607] [system] [taskboard] codex CLI exited: status=completed, exitCode=0, duration=565,6s",
            "[21:48:46.506] [system] [taskboard] Started codex CLI (PID 63216)",
            "[21:52:59.000] [stdout] [[TASK_DONE]]",
            "[21:53:06.256] [system] [taskboard] codex CLI exited: status=completed, exitCode=0, duration=259,8s");

        var decision = CompletionGate.Evaluate(status, log, priorReissues: 2, maxReissues: 2);

        Assert.Equal(CompletionGate.CompletionGateAction.Pass, decision.Action);
    }

    [Theory]
    // Guard the guards: genuine work that merely mentions approval or the gate
    // subject matter must still be reported.
    [InlineData("- [ ] Get approval from the design team, then wire the header.")]
    [InlineData("- [ ] The completion gate refactor is still pending.")]
    public void ExtractFindings_GenuineWorkNearSuppressedVocabulary_StillReported(string openItemLine)
    {
        var status = string.Join('\n', "## Open Items", openItemLine);

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.NotEmpty(findings);
    }

    [Fact]
    public void ExtractFindings_ApprovalGatedOpenItem_IsNotReported()
    {
        var status = string.Join('\n',
            "## Open Items",
            "- [ ] Publishing awaits the operator's approval.",
            "- [ ] Release remains locked pending explicit approval of the quickstart.");

        var findings = CompletionGate.ExtractFindings(status, recentLog: null);

        Assert.Empty(findings);
    }

    [Fact]
    public void Evaluate_NoFindings_Passes()
    {
        var decision = CompletionGate.Evaluate("Result: Success", recentLog: null, priorReissues: 0, maxReissues: MaxReissues);

        Assert.Equal(CompletionGate.CompletionGateAction.Pass, decision.Action);
        Assert.False(decision.IsIncomplete);
    }

    [Fact]
    public void Evaluate_FindingsWithBudgetLeft_Reissues()
    {
        var status = "## Open Items\n- [ ] finish the migration";

        var decision = CompletionGate.Evaluate(status, recentLog: null, priorReissues: 0, maxReissues: MaxReissues);

        Assert.Equal(CompletionGate.CompletionGateAction.Reissue, decision.Action);
        Assert.True(decision.IsIncomplete);
        Assert.NotEmpty(decision.Findings);
    }

    [Fact]
    public void Evaluate_FindingsWithBudgetExhausted_Escalates()
    {
        var status = "## Open Items\n- [ ] finish the migration";

        var decision = CompletionGate.Evaluate(status, recentLog: null, priorReissues: MaxReissues, maxReissues: MaxReissues);

        Assert.Equal(CompletionGate.CompletionGateAction.Escalate, decision.Action);
        Assert.True(decision.IsIncomplete);
    }

    [Fact]
    public void BuildFollowUp_RendersFindingsAsCheckboxes()
    {
        var followUp = CompletionGate.BuildFollowUp(new[] { "wire the route", "fix the build" });

        Assert.Contains("- [ ] wire the route", followUp);
        Assert.Contains("- [ ] fix the build", followUp);
        Assert.Contains("[[TASK_DONE]]", followUp);
        Assert.Contains("[[TASK_BLOCKED", followUp);
    }

    [Fact]
    public void BuildFollowUp_WithDossierSource_NamesTheDossierGapAndTheAlreadyIntegratedPath()
    {
        var followUp = CompletionGate.BuildFollowUp(
            new[] { "docs/x/index.html at attributed commit 2b5d24568: no markers" },
            source: CompletionGate.CompletionGateSource.DossierMaintenance);

        Assert.Contains("Dossier implementation log for this card is missing or invalid", followUp);
        Assert.Contains("\"Dossier implementation update\"", followUp);
        Assert.Contains("already on the integration branch", followUp);
        Assert.Contains("do not add it again", followUp);
        Assert.Contains("- [ ] docs/x/index.html at attributed commit 2b5d24568: no markers", followUp);
        Assert.DoesNotContain("unfinished work in the previous run's own result/status evidence", followUp);
    }

    [Fact]
    public void BuildFollowUp_WithoutSource_KeepsTheRunEvidenceWording()
    {
        var findings = new[] { "wire the route" };

        var followUp = CompletionGate.BuildFollowUp(findings);

        Assert.Equal(
            CompletionGate.BuildFollowUp(findings, source: CompletionGate.CompletionGateSource.RunEvidence),
            followUp);
        Assert.Contains("unfinished work in the previous run's own result/status evidence", followUp);
        Assert.DoesNotContain("Dossier", followUp);
    }

    /// <summary>
    /// ASS-734: the completion-gate follow-up must also lead with the diff-only
    /// steering rule and list the already-made commits when supplied, so the
    /// reissue resolves the open findings on top of existing work.
    /// </summary>
    [Fact]
    public void BuildFollowUp_LeadsWithDiffOnlyRule_AndListsPriorCommits()
    {
        var followUp = CompletionGate.BuildFollowUp(
            new[] { "wire the route" },
            priorCommits: new[] { "a1b2c3 feat: scaffold route" });

        Assert.StartsWith(RunOutcomePolicy.DiffOnlySteeringRule, followUp);
        Assert.Contains("Commits already made for this task", followUp);
        Assert.Contains("a1b2c3 feat: scaffold route", followUp);
        Assert.Contains("- [ ] wire the route", followUp);
    }
}
