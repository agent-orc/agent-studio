

using Xunit;

namespace AgentStudio.Tests;

public class RunOutcomeContractTests
{
    [Fact]
    public void ResultDocument_RecordsExactImmutableShaOnce()
    {
        var sha = new string('a', 40);
        var report = AgentStudio.Review.SummaryGenerationService.AppendImmutableResultSha(
            "# Status\n", sha);

        Assert.Contains($"- Immutable result SHA: `{sha}`", report);
        Assert.Equal(report,
            AgentStudio.Review.SummaryGenerationService.AppendImmutableResultSha(report, sha));
    }

    public static IEnumerable<object[]> TerminalCases()
    {
        yield return [AgentOutcomeKind.Done, "success", "Success"];
        yield return [AgentOutcomeKind.NoOp, "noop", "NoOp"];
        yield return [AgentOutcomeKind.Blocked, "blocked", "Blocked"];
        yield return [AgentOutcomeKind.NeedsInput, "needs-input", "NeedsInput"];
    }

    [Theory]
    [MemberData(nameof(TerminalCases))]
    public void SentinelOutcome_DrivesLaneProtocolAndToastFromOneClassification(
        AgentOutcomeKind kind,
        string expectedKind,
        string expectedProtocolResult)
    {
        var outcome = new AgentOutcome(
            Kind: kind,
            Summary: $"sentinel {kind}",
            MatchedSentinel: true,
            SentinelKeyword: kind.ToString().ToUpperInvariant(),
            Reason: null,
            AgentTextChars: 20,
            OutputLineCount: 2,
            DurationSeconds: 3.5);

        var terminal = TerminalRunOutcomeClassifier.Classify(RunStatuses.Failed, outcome);

        Assert.Equal(expectedKind, terminal.Kind);
        Assert.Equal(expectedProtocolResult, terminal.ProtocolResult);
        Assert.True(RunCompletionPolicy.ShouldMoveToReview(terminal));
        Assert.False(terminal.ShouldShowFailureToast);

        var protocol = AgentStudio.Review.SummaryGenerationService.ApplyOutcomeResultLine(
            "# Status\n\n- Result: Failed\n- Duration: 4 sec\n",
            terminal.ProtocolResult);
        Assert.Contains($"- Result: {expectedProtocolResult}", protocol);
    }

    [Fact]
    public void FailedRunWithoutTerminalSignal_StaysFailedEverywhere()
    {
        var outcome = new AgentOutcome(
            Kind: AgentOutcomeKind.Unknown,
            Summary: "crash",
            MatchedSentinel: false,
            SentinelKeyword: null,
            Reason: "no terminal signal",
            AgentTextChars: 0,
            OutputLineCount: 1,
            DurationSeconds: 3.5);

        var terminal = TerminalRunOutcomeClassifier.Classify(RunStatuses.Failed, outcome);

        Assert.Equal("failed", terminal.Kind);
        Assert.Equal("Failed", terminal.ProtocolResult);
        Assert.False(terminal.ShouldMoveToReview);
        Assert.True(terminal.ShouldShowFailureToast);
    }

    [Fact]
    public void EmptyFastExit_CompletedStatus_IsFailedStartNotNoOp()
    {
        var terminal = TerminalRunOutcomeClassifier.Classify(
            RunStatuses.Completed,
            new List<CliOutputLine>(),
            durationSeconds: 1.2,
            exitCode: 0);

        Assert.Equal(TerminalRunOutcomeKinds.Failed, terminal.Kind);
        Assert.Equal("Failed", terminal.ProtocolResult);
        Assert.False(terminal.ShouldMoveToReview);
        Assert.True(terminal.ShouldShowFailureToast);
        Assert.Equal(RunStatuses.Failed, TerminalRunOutcomeClassifier.ExecutionStatusFor(terminal, RunStatuses.Completed));
        Assert.Contains("exitCode=0", terminal.Reason);
        Assert.Contains("failed start", terminal.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyFastExit_DeliberatelyStopped_IsInterruptedNotFailedStart()
    {
        var terminal = TerminalRunOutcomeClassifier.Classify(
            RunStatuses.Stopped,
            new List<CliOutputLine>(),
            durationSeconds: 0.1,
            exitCode: -1);

        Assert.Equal(TerminalRunOutcomeKinds.Interrupted, terminal.Kind);
        Assert.False(terminal.ShouldMoveToReview);
        Assert.False(terminal.ShouldShowFailureToast);
        Assert.Equal(
            RunStatuses.Stopped,
            TerminalRunOutcomeClassifier.ExecutionStatusFor(terminal, RunStatuses.Stopped));
    }

    // Regression for the bug "Codex exit=-1 trotz erfolgreichem Commit landet in
    // 4-auto-review verdict=reissue + ATP-manual": a run that committed real work
    // (commitCount > 0) but then exited -1 because its downstream test runs were
    // killed must NOT be treated as a hard failure. It is an honest "partial":
    // route it to review (4-auto-review) with a Partial verdict and no crash
    // toast, so auto-continuous mode is preserved and the successful commit is
    // not silently discarded by a reissue loop.
    //
    // AC#1: exit=-1 + commitCount > 0 is not classified as hard-fail.
    // AC#3: expect the card in 4-auto-review with a partial (not reissue) verdict.
    [Fact]
    public void FailedRunThatCommitted_RoutesToReviewAsCommittedPartial()
    {
        var outcome = new AgentOutcome(
            Kind: AgentOutcomeKind.Unknown,
            Summary: "tests killed after commit",
            MatchedSentinel: false,
            SentinelKeyword: null,
            Reason: "no terminal signal",
            AgentTextChars: 240,
            OutputLineCount: 30,
            DurationSeconds: 42.0);

        var terminal = TerminalRunOutcomeClassifier.Classify(
            RunStatuses.Failed, outcome, commitsDuringRun: 2);

        Assert.Equal("committed-partial", terminal.Kind);
        Assert.Equal("Partial", terminal.ProtocolResult);
        Assert.True(terminal.ShouldMoveToReview);
        Assert.True(RunCompletionPolicy.ShouldMoveToReview(terminal));
        Assert.False(terminal.ShouldShowFailureToast);
    }

    // The commit-aware branch must not soften an honest crash: a failed run that
    // committed nothing (commitCount == 0) stays a hard failure everywhere. This
    // pins the boundary so the fix above cannot drift into swallowing real crashes.
    [Fact]
    public void FailedRunWithZeroCommits_StaysHardFailure()
    {
        var outcome = new AgentOutcome(
            Kind: AgentOutcomeKind.Unknown,
            Summary: "crash",
            MatchedSentinel: false,
            SentinelKeyword: null,
            Reason: "no terminal signal",
            AgentTextChars: 0,
            OutputLineCount: 1,
            DurationSeconds: 3.5);

        var terminal = TerminalRunOutcomeClassifier.Classify(
            RunStatuses.Failed, outcome, commitsDuringRun: 0);

        Assert.Equal("failed", terminal.Kind);
        Assert.Equal("Failed", terminal.ProtocolResult);
        Assert.False(terminal.ShouldMoveToReview);
        Assert.True(terminal.ShouldShowFailureToast);
    }

    // Committed-partial hardening (AC#1 / AC#3): a failed run whose agent text
    // could not be mapped to a terminal verdict (RunIssueKind.OrchestratorInconclusive)
    // but which committed real work must surface at the terminal layer as an
    // honest "committed-partial" - routed to review, Partial verdict, and NO
    // crash toast. The inconclusive cause is carried by the TASK-level
    // RunOutcomePolicy (NotifyUserAndStop); the per-run terminal report must
    // not pile a hard FAILURE on top of the committed work.
    [Fact]
    public void OrchestratorInconclusiveShape_WithCommits_IsCommittedPartial_NotHardFailure()
    {
        var outcome = new AgentOutcome(
            Kind: AgentOutcomeKind.Unknown,
            Summary: "long investigation, no recognizable sign-off",
            MatchedSentinel: false,
            SentinelKeyword: null,
            Reason: "no sentinel matched, heuristic also inconclusive",
            AgentTextChars: 600,
            OutputLineCount: 40,
            DurationSeconds: 120.0)
        {
            IssueKind = RunIssueKind.OrchestratorInconclusive
        };

        var terminal = TerminalRunOutcomeClassifier.Classify(
            RunStatuses.Failed, outcome, commitsDuringRun: 1);

        Assert.Equal("committed-partial", terminal.Kind);
        Assert.Equal("Partial", terminal.ProtocolResult);
        Assert.True(terminal.ShouldMoveToReview);
        Assert.True(RunCompletionPolicy.ShouldMoveToReview(terminal));
        Assert.False(terminal.ShouldShowFailureToast);
    }

    // Layer separation pin: with zero commits the terminal layer still reports
    // the run itself as "failed" (honest per-RUN report, crash toast on). This
    // is deliberately NOT the same as a terminal user-visible TASK failure - the
    // task-level handling of an inconclusive run lives in RunOutcomePolicy
    // (NotifyUserAndStop), not here. This test exists so a future change cannot
    // quietly collapse the two layers by softening the per-run report and
    // calling the hardening "done".
    [Fact]
    public void OrchestratorInconclusiveShape_ZeroCommits_TerminalLayerReportsRunFailed()
    {
        var outcome = new AgentOutcome(
            Kind: AgentOutcomeKind.Unknown,
            Summary: "long investigation, no recognizable sign-off",
            MatchedSentinel: false,
            SentinelKeyword: null,
            Reason: "no sentinel matched, heuristic also inconclusive",
            AgentTextChars: 600,
            OutputLineCount: 40,
            DurationSeconds: 120.0)
        {
            IssueKind = RunIssueKind.OrchestratorInconclusive
        };

        var terminal = TerminalRunOutcomeClassifier.Classify(
            RunStatuses.Failed, outcome, commitsDuringRun: 0);

        Assert.Equal("failed", terminal.Kind);
        Assert.Equal("Failed", terminal.ProtocolResult);
        Assert.False(terminal.ShouldMoveToReview);
        Assert.True(terminal.ShouldShowFailureToast);
    }

    // Drive-to-conclusion step 1 acceptance: the legacy "classifier-unknown"
    // verdict is fully retired from live emission. The enum member must be gone
    // from both the issue-kind and the chat-surface enums, and NO live surface
    // (chat tag or bus topic) may emit the string "classifier-unknown". The new
    // InfraCrash / OrchestratorInconclusive kinds replace it. (Archived on-disk
    // logs that still carry the old chip are recognised by TaskScannerService
    // for backward-compatible rendering; that is a separate, deliberate path and
    // is not "live emission".)
    [Fact]
    public void ClassifierUnknown_IsFullyRetiredFromLiveEmission()
    {
        Assert.DoesNotContain("ClassifierUnknown", Enum.GetNames<RunIssueKind>());
        Assert.DoesNotContain("ClassifierUnknown", Enum.GetNames<OrchestratorMessageKind>());

        foreach (var kind in Enum.GetValues<OrchestratorMessageKind>())
        {
            Assert.NotEqual("classifier-unknown", kind.ToTag());
            Assert.NotEqual("classifier-unknown", kind.ToBusTopic());
        }

        // The replacements are present and wired to their honest tags/topics.
        Assert.Equal("infra-crash", OrchestratorMessageKind.InfraCrash.ToTag());
        Assert.Equal("infra-crash", OrchestratorMessageKind.InfraCrash.ToBusTopic());
        Assert.Equal("orchestrator-inconclusive", OrchestratorMessageKind.OrchestratorInconclusive.ToTag());
        Assert.Equal("orchestrator-inconclusive", OrchestratorMessageKind.OrchestratorInconclusive.ToBusTopic());
    }

    // The bug this guards: five identical Codex runs (all exitCode=-1, all
    // [[TASK_NOOP]]) produced three different status.md values, four different
    // target lanes, and an inconsistent failure toast, because lane routing,
    // status.md, and the toast each classified the run independently. The
    // existing cases above pin the synthetic-AgentOutcome layer; this drives
    // the real rendered cli-output.log path (TryClassifyRenderedLog), which is
    // where the exitCode=-1 exit line actually enters the classifier. A hard
    // sentinel must win over the Windows kill artifact (exitCode=-1) so every
    // consumer derives the same answer from one classification.
    public static IEnumerable<object[]> CodexExitMinusOneCases()
    {
        // sentinel, expected wire kind, expected protocol result, moves to review, failure toast
        yield return ["[[TASK_NOOP]]", "noop", "NoOp", true, false];
        yield return ["[[TASK_DONE]]", "success", "Success", true, false];
        yield return ["[[TASK_BLOCKED:cannot reach API]]", "blocked", "Blocked", true, false];
        yield return ["[[TASK_NEEDS_INPUT:which file?]]", "needs-input", "NeedsInput", true, false];
    }

    [Theory]
    [MemberData(nameof(CodexExitMinusOneCases))]
    public void CodexExitMinusOne_WithSentinel_ClassifiesIdenticallyAcrossAllConsumers(
        string sentinel,
        string expectedKind,
        string expectedProtocolResult,
        bool expectedMovesToReview,
        bool expectedFailureToast)
    {
        // A rendered cli-output.log tail exactly as it lands on disk: agent
        // text on stdout, then the synthetic taskboard exit line reporting the
        // Windows kill artifact (status=failed, exitCode=-1).
        var renderedLog = string.Join("\n",
            "[2026-06-01T18:00:00.000Z] [stdout] Looked at the repository state for this task.",
            $"[2026-06-01T18:00:01.000Z] [stdout] {sentinel}",
            "[2026-06-01T18:00:05.000Z] [system] [taskboard] codex CLI exited: status=failed, exitCode=-1, duration=4.2s");

        var classified = TerminalRunOutcomeClassifier.TryClassifyRenderedLog(renderedLog);
        Assert.NotNull(classified);
        var terminal = classified!.Value.Outcome;

        // The sentinel was recognised despite the failed/-1 process exit.
        Assert.True(classified.Value.AgentOutcome.MatchedSentinel);

        // One classification, read three ways - the divergence killer.
        Assert.Equal(expectedKind, terminal.Kind);                                  // toast wire value (execution.runOutcome)
        Assert.Equal(expectedProtocolResult, terminal.ProtocolResult);              // status.md "- Result:" line
        Assert.Equal(expectedMovesToReview, terminal.ShouldMoveToReview);           // lane routing
        Assert.Equal(expectedMovesToReview, RunCompletionPolicy.ShouldMoveToReview(terminal));
        Assert.Equal(expectedFailureToast, terminal.ShouldShowFailureToast);        // failure toast/modal gating

        // status.md is forced to the same outcome, never left at the Haiku guess.
        var protocol = AgentStudio.Review.SummaryGenerationService.ApplyOutcomeResultLine(
            "# Status\n\n- Result: Failed\n- Duration: 4 sec\n",
            terminal.ProtocolResult);
        Assert.Contains($"- Result: {expectedProtocolResult}", protocol);
    }

    [Fact]
    public void CodexExitMinusOne_NoOp_DoesNotDivergeFromSentinelOnlyClassification()
    {
        // Pin the (exitCode, reason) -> status step the rendered-log path
        // depends on: Windows hands back -1 on a natural process death, so the
        // raw status is "failed", yet the [[TASK_NOOP]] sentinel still wins.
        var status = RunStatusClassifier.Classify(-1, RunStopReason.None);
        Assert.Equal(RunStatuses.Failed, status);

        var lines = new List<CliOutputLine>
        {
            new() { Stream = "stdout", Text = "No changes were necessary." },
            new() { Stream = "stdout", Text = "[[TASK_NOOP]]" }
        };

        var terminal = TerminalRunOutcomeClassifier.Classify(status, lines, durationSeconds: 4.2);

        Assert.Equal("noop", terminal.Kind);
        Assert.Equal("NoOp", terminal.ProtocolResult);
        Assert.True(terminal.ShouldMoveToReview);
        Assert.False(terminal.ShouldShowFailureToast);
    }

    [Fact]
    public void RenderedLog_CompletedEmptyFastExit_IsFailedStart()
    {
        var renderedLog = "[2026-06-01T18:00:05.000Z] [system] [taskboard] codex CLI exited: status=completed, exitCode=0, duration=1.2s";

        var classified = TerminalRunOutcomeClassifier.TryClassifyRenderedLog(renderedLog);

        Assert.NotNull(classified);
        Assert.Equal(RunIssueKind.EmptyFastExit, classified!.Value.AgentOutcome.IssueKind);
        Assert.Equal(TerminalRunOutcomeKinds.Failed, classified.Value.Outcome.Kind);
        Assert.False(classified.Value.Outcome.ShouldMoveToReview);
        Assert.Contains("exitCode=0", classified.Value.AgentOutcome.Summary ?? string.Empty);
    }

    [Fact]
    public void SummaryResultLine_IsForcedToCanonicalOutcome()
    {
        var summary = """
            # Status

            - Result: Failed
            - Duration: 4 sec

            ## What Was Done
            - Nothing.

            ## Open Items
            - None.
            """;

        var updated = AgentStudio.Review.SummaryGenerationService.ApplyOutcomeResultLine(summary, "NoOp");

        Assert.Contains("- Result: NoOp", updated);
        Assert.DoesNotContain("- Result: Failed", updated);
    }

    [Fact]
    public void SummaryResultLine_RewritePreservesProtocolImagesSection()
    {
        var summary = """
            # Status

            - Result: Failed
            - Duration: 4 sec

            ## What Was Done
            - Captured screenshots and reviewed the supplied image.

            ## Open Items
            - None.

            ## Images
            - ![](results/run-proof.png)
            - ![](results/playwright/spec-name/nested-proof.png)
            - ![](attachments/input-wireframe.png)
            """;

        var updated = AgentStudio.Review.SummaryGenerationService.ApplyOutcomeResultLine(summary, "Success");

        Assert.Contains("- Result: Success", updated);
        Assert.DoesNotContain("- Result: Failed", updated);
        Assert.Contains("## Images", updated);
        Assert.Contains("![](results/run-proof.png)", updated);
        Assert.Contains("![](results/playwright/spec-name/nested-proof.png)", updated);
        Assert.Contains("![](attachments/input-wireframe.png)", updated);
        Assert.Equal(1, updated.Split("## Images", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void SummaryProtocolImages_AreAppendedFromLogWhenModelOmitsThem()
    {
        var jobFolder = NewTempJobFolder();
        try
        {
            WriteJobFile(jobFolder, "results/run-proof.png");
            WriteJobFile(jobFolder, "results/playwright/spec-name/nested-proof.png");
            WriteJobFile(jobFolder, "attachments/input-wireframe.png");

            var summary = """
                # Status

                - Result: Failed
                - Duration: 4 sec

                ## What Was Done
                - Captured screenshots and reviewed the supplied image.

                ## Open Items
                - None.
                """;
            var log = string.Join('\n',
                "Captured result screenshot at results/run-proof.png.",
                "Captured duplicate result screenshot at results/run-proof.png.",
                "Nested Playwright artifact: results/playwright/spec-name/nested-proof.png",
                new string('x', 65_000),
                "User supplied reference: attachments/input-wireframe.png",
                "Ignore non-image artifact: results/review-evidence.jsonl");

            var updated = AgentStudio.Review.SummaryGenerationService.ApplyOutcomeResultLine(summary, "Success");
            updated = AgentStudio.Review.SummaryGenerationService.ApplyProtocolImageReferences(updated, log, jobFolder, out var appendedCount);

            Assert.Contains("- Result: Success", updated);
            Assert.DoesNotContain("- Result: Failed", updated);
            Assert.Contains("## Images", updated);
            Assert.Contains("![](results/run-proof.png)", updated);
            Assert.Contains("![](results/playwright/spec-name/nested-proof.png)", updated);
            Assert.Contains("![](attachments/input-wireframe.png)", updated);
            Assert.DoesNotContain("review-evidence", updated);
            Assert.Equal(3, appendedCount);
            Assert.Equal(1, updated.Split("![](results/run-proof.png)", StringSplitOptions.None).Length - 1);
        }
        finally
        {
            Directory.Delete(jobFolder, recursive: true);
        }
    }

    [Fact]
    public void SummaryProtocolImages_ExampleAndGlobPaths_AreNotInjected()
    {
        // Reproduces the AGT-1938 bug: the Artifact-Upload card's log is full of
        // documentation/example image paths - including a literal glob - none of
        // which exist under the job folder. Nothing must be injected, so the
        // protocol never grows a run of empty image rows.
        var jobFolder = NewTempJobFolder();
        try
        {
            var summary = """
                # Status

                - Result: Success
                - Duration: 4 sec

                ## What Was Done
                - Documented how artifact upload references screenshots.
                """;
            var log = string.Join('\n',
                "e.g. results/playwright/auth-spec/screenshot.png",
                "e.g. results/screenshots/compose-steer.png",
                "e.g. results/foo.png",
                "e.g. results/playwright/spec/file.png",
                "glob example: results/*.png",
                "e.g. results/proof.png");

            var updated = AgentStudio.Review.SummaryGenerationService.ApplyProtocolImageReferences(summary, log, jobFolder, out var appendedCount);

            Assert.Equal(0, appendedCount);
            Assert.DoesNotContain("## Images", updated);
            Assert.DoesNotContain("![]", updated);
            Assert.Equal(summary, updated);
        }
        finally
        {
            Directory.Delete(jobFolder, recursive: true);
        }
    }

    [Fact]
    public void SummaryProtocolImages_MixOfRealAndExamplePaths_InjectsOnlyExisting()
    {
        var jobFolder = NewTempJobFolder();
        try
        {
            WriteJobFile(jobFolder, "results/run-proof.png");

            var summary = """
                # Status

                - Result: Success
                - Duration: 4 sec

                ## What Was Done
                - Captured one real screenshot; mentioned some examples.
                """;
            var log = string.Join('\n',
                "Real screenshot saved to results/run-proof.png",
                "e.g. results/foo.png",
                "glob example: results/*.png",
                "missing nested: results/playwright/spec/file.png");

            var updated = AgentStudio.Review.SummaryGenerationService.ApplyProtocolImageReferences(summary, log, jobFolder, out var appendedCount);

            Assert.Equal(1, appendedCount);
            Assert.Contains("## Images", updated);
            Assert.Contains("![](results/run-proof.png)", updated);
            Assert.DoesNotContain("results/foo.png", updated);
            Assert.DoesNotContain("results/playwright/spec/file.png", updated);
        }
        finally
        {
            Directory.Delete(jobFolder, recursive: true);
        }
    }

    private static string NewTempJobFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agt-summary-images-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteJobFile(string jobFolder, string relativePath)
    {
        var full = Path.Combine(jobFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, [0x89, 0x50, 0x4E, 0x47]); // PNG magic; content is irrelevant to existence checks
    }
}
