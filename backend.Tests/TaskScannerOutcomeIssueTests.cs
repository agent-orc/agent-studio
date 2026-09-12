using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Read-time derivation of <see cref="TaskInfo.OutcomeIssue"/> from
/// <c>logs/cli-output.log</c>. Locks in the "Erfolg sieht aus wie
/// classifier-unknown" fix (ASS-775): an accepted/completed task must not
/// surface a stale Warn-class outcome chip, and the chip summary must never be
/// sourced from an orchestrator decision/meta line.
/// </summary>
public class TaskScannerOutcomeIssueTests : IDisposable
{
    private readonly string _watchPath;

    public TaskScannerOutcomeIssueTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-outcome-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    private TaskScannerService BuildScanner()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
    }

    private void SeedJob(string slug, string state, string log)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug} title\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\"}}");
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"), log);
    }

    private TaskOutcomeIssue? Outcome(string slug)
        => BuildScanner().FindJob(slug, _watchPath)?.OutcomeIssue;

    [Fact]
    public void GenuineClassifierUnknownMarker_OnProgressCard_StillSurfaces()
    {
        // Baseline: a genuine typed classifier-unknown line on a live card is
        // still derived (the fix must not blanket-suppress the chip).
        SeedJob("live", TaskStates.AutoReview,
            $"[12:00:00.000] [stdout] working{Environment.NewLine}" +
            $"[12:00:05.000] [orchestrator] [classifier-unknown] The run could not be classified after one orchestrator intervention.{Environment.NewLine}");

        var issue = Outcome("live");

        Assert.NotNull(issue);
        Assert.Equal("classifier-unknown", issue!.Kind);
    }

    [Fact]
    public void AcceptDecisionLine_AfterClassifierUnknown_SupersedesTheStaleChip()
    {
        // ASS-775 shape: an intermediate-cycle classifier-unknown followed by the
        // orchestrator's accept note. The accept supersedes the stale chip.
        SeedJob("accepted", TaskStates.Completed,
            $"[09:10:00.000] [orchestrator] [classifier-unknown] could not classify the agent's reply{Environment.NewLine}" +
            $"[09:15:33.000] [orchestrator] [decision] Auto-review accepted \"do the thing\" as done. Moved to 5-human-review for your approval. Reason: classifier-unknown earlier{Environment.NewLine}");

        Assert.Null(Outcome("accepted"));
    }

    [Fact]
    public void Ass775_VerbatimProductionShape_DerivesNoChip_OnCompletedOrHumanReview()
    {
        // ASS-775 grounded in the *verbatim* production line formats observed in
        // the workspace logs: a typed classifier-unknown marker from an
        // intermediate cycle, then the orchestrator's real "accepted with concerns
        // ... Moved to 5-human-review for your approval" decision note. The stale
        // chip — whose summary was wrongly sourced from that decision note — must
        // no longer be derived, on the 6-completed card OR the 5-human-review card.
        var classifierUnknown =
            "[08:53:55.333] [orchestrator] [classifier-unknown] Could not classify the agent's reply after deterministic checks. (category: classifier-unknown; run summary: Agent text did not match any known shape.)";
        var acceptWithConcerns =
            "[09:15:33.000] [orchestrator] [decision] Auto-review accepted \"BUG: classifier-unknown routing\" with concerns (1 of 4 aspects flagged). Moved to 5-human-review for your approval.";
        var log = classifierUnknown + Environment.NewLine + acceptWithConcerns + Environment.NewLine;

        SeedJob("ass775-completed", TaskStates.Completed, log);
        SeedJob("ass775-human", TaskStates.HumanReview, log);

        Assert.Null(Outcome("ass775-completed"));
        Assert.Null(Outcome("ass775-human"));
    }

    [Fact]
    public void DecisionLineMentioningClassifierUnknown_IsNeverDerivedAsAnOutcome()
    {
        // The summary must never come from an orchestrator decision/meta line:
        // a [decision] line whose accept reason mentions "classifier-unknown" is
        // not itself a runner outcome.
        SeedJob("decision-only", TaskStates.HumanReview,
            $"[09:15:33.000] [orchestrator] [decision] Auto-review accepted \"x\" as done. Moved to 5-human-review. Reason: prior run was classifier-unknown{Environment.NewLine}");

        Assert.Null(Outcome("decision-only"));
    }

    [Fact]
    public void ReissueMetaLineMentioningClassifierUnknown_IsNotAnOutcome()
    {
        SeedJob("reissue-meta", TaskStates.AutoReview,
            $"[09:15:33.000] [orchestrator] [reissue] Re-issuing once because the previous run was classifier-unknown.{Environment.NewLine}");

        Assert.Null(Outcome("reissue-meta"));
    }

    [Fact]
    public void CompletedCard_WithStaleClassifierUnknownButNoAcceptLine_StillSuppresses()
    {
        // Defensive: the accept note has scrolled out of the read tail (or never
        // logged), but a 6-completed card is terminal-done = accepted, so the
        // Warn-class chip is still suppressed.
        SeedJob("completed-stale", TaskStates.Completed,
            $"[09:10:00.000] [orchestrator] [classifier-unknown] could not classify the agent's reply{Environment.NewLine}");

        Assert.Null(Outcome("completed-stale"));
    }

    [Fact]
    public void HumanReviewCard_WithClassifierUnknownAndNoAcceptLine_StillSurfaces()
    {
        // 5-human-review is ambiguous (accept-to-human OR escalate). Without an
        // accept note in the log the scanner keeps the chip; the endpoint overlay
        // / backfill reconcile it only when the verdict is actually accept.
        SeedJob("human-review-open", TaskStates.HumanReview,
            $"[09:10:00.000] [orchestrator] [classifier-unknown] could not classify the agent's reply{Environment.NewLine}");

        var issue = Outcome("human-review-open");

        Assert.NotNull(issue);
        Assert.Equal("classifier-unknown", issue!.Kind);
    }

    [Fact]
    public void HighSeverityBlocker_OnCompletedCard_IsNotSuppressed()
    {
        // Only Warn-class ambiguity outcomes are verdict-contradicting. A real
        // host failure stays visible even on a terminal card.
        SeedJob("completed-blocker", TaskStates.Completed,
            $"[09:10:00.000] [orchestrator] [environment-blocker] sandbox refused to let the agent execute{Environment.NewLine}");

        var issue = Outcome("completed-blocker");

        Assert.NotNull(issue);
        Assert.Equal("environment-blocker", issue!.Kind);
    }

    [Fact]
    public void WatchdogTimeout_PreservesCompleteTechnicalDetailsBeyondCompactSummary()
    {
        var diagnostic =
            "[orchestrator] [watchdog-timeout] \"A deliberately long task title that keeps the diagnostic line over the compact summary limit\" (codex): " +
            "auto-cancelled after 601s of silence. The run will finalize as failed. " +
            "[phase=TurnCompleted silence=601s allowed=600s lastActivity=2026-07-22T09:10:00.000Z " +
            "session=019c123456789abcdef0123456789abcdef runner=agent-runner-with-a-long-diagnostic-name]";
        SeedJob("watchdog-details", TaskStates.HumanReview,
            $"[09:20:01.000] {diagnostic}{Environment.NewLine}");

        var issue = Outcome("watchdog-details");

        Assert.NotNull(issue);
        Assert.Equal("watchdog-timeout", issue!.Kind);
        Assert.Equal(diagnostic, issue.TechnicalDetails);
        Assert.EndsWith("...", issue.Summary);
        Assert.DoesNotContain("runner=agent-runner-with-a-long-diagnostic-name", issue.Summary);
    }

    [Theory]
    [InlineData("tool-router-error", "Tool router error")]
    [InlineData("no-reply", "No reply")]
    public void TypedAgentFailureMarker_SurfacesWithCompleteTechnicalDetails(string kind, string expectedLabel)
    {
        var diagnostic =
            $"[orchestrator] [{kind}] Agent execution failed. " +
            "[phase=TurnCompleted silence=42s allowed=600s complete-tail]";
        SeedJob(kind, TaskStates.HumanReview,
            $"[09:20:01.000] {diagnostic}{Environment.NewLine}");

        var issue = Outcome(kind);

        Assert.NotNull(issue);
        Assert.Equal(kind, issue!.Kind);
        Assert.Equal(expectedLabel, issue.Label);
        Assert.Equal(diagnostic, issue.TechnicalDetails);
    }

    [Fact]
    public void EmptyFastExitMarker_SurfacesHighSeverityOutcome()
    {
        SeedJob("empty-fast-exit", TaskStates.HumanReview,
            $"[09:10:00.000] [orchestrator] [empty-fast-exit] The agent CLI exited almost immediately without producing an agent turn; treating this as a failed start, not as [[TASK_NOOP]].{Environment.NewLine}");

        var issue = Outcome("empty-fast-exit");

        Assert.NotNull(issue);
        Assert.Equal("empty-fast-exit", issue!.Kind);
        Assert.Equal("High", issue.Severity);
    }

    [Fact]
    public void RunLostAcrossRestartMarker_SurfacesDistinctHighSeverityOutcome()
    {
        SeedJob("restart-loss", TaskStates.HumanReview,
            $"[09:10:00.000] [system] [taskboard] run lost across restart: worker PID 412 no longer exists{Environment.NewLine}");

        var issue = Outcome("restart-loss");

        Assert.NotNull(issue);
        Assert.Equal("run-lost-across-restart", issue!.Kind);
        Assert.Equal("Run lost across restart", issue.Label);
        Assert.Equal("High", issue.Severity);
    }

    [Fact]
    public void WorktreeContainmentMarker_SurfacesHighSeverityOutcome()
    {
        SeedJob("worktree-containment", TaskStates.HumanReview,
            $"[09:10:00.000] [orchestrator] [worktree-containment] Worktree run job-1 changed the shared main checkout; verdict=main-checkout-modified{Environment.NewLine}");

        var issue = Outcome("worktree-containment");

        Assert.NotNull(issue);
        Assert.Equal("worktree-containment", issue!.Kind);
        Assert.Equal("High", issue.Severity);
    }

    [Fact]
    public void AgentGitViolationMarker_OnInterventionLine_SurfacesHighSeverityOutcome()
    {
        SeedJob("agent-git-violation", TaskStates.HumanReview,
            $"[09:10:00.000] [orchestrator] [intervention] [agent-git-violation] Genuine git damage detected: worker push changed a protected remote branch.{Environment.NewLine}");

        var issue = Outcome("agent-git-violation");

        Assert.NotNull(issue);
        Assert.Equal("agent-git-violation", issue!.Kind);
        Assert.Equal("High", issue.Severity);
    }

    [Fact]
    public void WorkerHeadAdvancedMarker_SurfacesInfoCleanupHint()
    {
        SeedJob("worker-head-advanced", TaskStates.AutoReview,
            $"[09:10:00.000] [orchestrator] [worker-head-advanced] INFO: worker advanced HEAD - needs cleanup; pipeline continues.{Environment.NewLine}");

        var issue = Outcome("worker-head-advanced");

        Assert.NotNull(issue);
        Assert.Equal("worker-head-advanced", issue!.Kind);
        Assert.Equal("Worker advanced HEAD", issue.Label);
        Assert.Equal("Info", issue.Severity);
        Assert.Contains("needs cleanup", issue.Summary);
    }

    [Fact]
    public void IntegrationConflictMarker_SurfacesHighSeverityOutcomeWithFiles()
    {
        SeedJob("integration-conflict", TaskStates.AutoReview,
            $"[09:10:00.000] [orchestrator] [integration-conflict] Worktree branch integration is blocked by a merge conflict. Task branch `task/ASS-111` was not merged into `develop`. Worktree: `C:\\temp\\ass-worktrees\\ASS-111`. Conflicted files: frontend/src/app/tree.ts. Error: could not apply commit{Environment.NewLine}");

        var issue = Outcome("integration-conflict");

        Assert.NotNull(issue);
        Assert.Equal("integration-conflict", issue!.Kind);
        Assert.Equal("High", issue.Severity);
        Assert.Contains("frontend/src/app/tree.ts", issue.Summary);
    }

    [Fact]
    public void IntegrationErrorMarker_SurfacesHighSeverityOutcome()
    {
        SeedJob("integration-error", TaskStates.AutoReview,
            $"[09:10:00.000] [orchestrator] [integration-error] Worktree branch integration failed with outcome `Error`. Task branch `task/ASS-112` was not merged into `develop`. Worktree: `C:\\temp\\ass-worktrees\\ASS-112`. Conflicted files: none reported. Error: ff-only failed{Environment.NewLine}");

        var issue = Outcome("integration-error");

        Assert.NotNull(issue);
        Assert.Equal("integration-error", issue!.Kind);
        Assert.Equal("High", issue.Severity);
    }

    [Fact]
    public void TaskBranchUnpushedMarker_SurfacesWarnOutcome()
    {
        SeedJob("task-branch-unpushed", TaskStates.AutoReview,
            $"[09:10:00.000] [orchestrator] [task-branch-unpushed] Task branch `task/ASS-1666` could not be pushed to `origin` after retry. Push status: failed. Error: network unavailable{Environment.NewLine}");

        var issue = Outcome("task-branch-unpushed");

        Assert.NotNull(issue);
        Assert.Equal("task-branch-unpushed", issue!.Kind);
        Assert.Equal("Warn", issue.Severity);
        Assert.Contains("network unavailable", issue.Summary);
    }

    [Fact]
    public void AcceptedCard_WithTaskBranchUnpushed_KeepsPortabilityWarning()
    {
        SeedJob("accepted-unpushed", TaskStates.HumanReview,
            $"[09:10:00.000] [orchestrator] [task-branch-unpushed] Task branch `task/ASS-1666` could not be pushed to `origin` after retry. Push status: failed. Error: network unavailable{Environment.NewLine}" +
            $"[09:15:33.000] [orchestrator] [decision] Auto-review accepted \"x\" as done. Moved to 5-human-review for your approval.{Environment.NewLine}");

        var issue = Outcome("accepted-unpushed");

        Assert.NotNull(issue);
        Assert.Equal("task-branch-unpushed", issue!.Kind);
        Assert.Equal("Warn", issue.Severity);
    }

    [Fact]
    public void CompletedCard_WithTaskBranchUnpushed_KeepsPortabilityWarning()
    {
        SeedJob("completed-unpushed", TaskStates.Completed,
            $"[09:10:00.000] [orchestrator] [task-branch-unpushed] Task branch `task/ASS-1666` could not be pushed to `origin` after retry. Push status: failed. Error: network unavailable{Environment.NewLine}");

        var issue = Outcome("completed-unpushed");

        Assert.NotNull(issue);
        Assert.Equal("task-branch-unpushed", issue!.Kind);
        Assert.Equal("Warn", issue.Severity);
    }

    [Fact]
    public void AgentStdoutMentioningContainmentInProse_DoesNotFalselySurface()
    {
        // ASS-914 regression (scanner self-reference): a self-modifying task whose
        // AGENT stdout merely *describes* the pipeline — the step is literally
        // named `worktree-containment` — must NOT be mislabelled as a containment
        // violation. Only the bracketed runner marker ([worktree-containment])
        // counts; a bare mention in [stdout] prose does not.
        SeedJob("self-ref", TaskStates.AutoReview,
            $"[12:00:00.000] [stdout] The standard pipeline includes both git slots (`worktree-containment`, `git-commit-attribution`) plus wiki maintenance.{Environment.NewLine}" +
            $"[12:00:05.000] [stdout] Added the deterministic build/test gate post-step; the older count test predates these additions.{Environment.NewLine}");

        Assert.Null(Outcome("self-ref"));
    }
}
