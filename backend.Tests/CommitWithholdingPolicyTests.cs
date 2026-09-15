using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix over the pure halves of the WEB-21 fix: what a refused commit
/// candidate gate withheld (<see cref="CommitWithholdingPolicy"/>), and what a
/// card parked while carrying that refusal is supposed to say
/// (<see cref="ParkedBlockerCatalog"/>).
///
/// <para>WEB-21 (15.09.2026) captured twelve screenshots, updated two files,
/// committed nothing, and landed in 5e-escalated with cause
/// <c>no-completion-signal</c> and an EMPTY parked reason. The card could not
/// tell an operator that a complete delivery was waiting uncommitted. These
/// rows pin the sentence and the file list that were missing.</para>
/// </summary>
public sealed class CommitWithholdingPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void From_ReportsWhatTheCommitWouldHaveContained()
    {
        var gate = Gate(
            decision: CommitGateDecisions.Warn,
            canCommit: false,
            candidates:
            [
                Candidate("results/WEB-21/shot-01.png", included: true, size: 2048),
                Candidate("status.md", included: true, size: 120),
                Candidate(".tmp-scratch.mjs", included: false, size: 40, exclusion: "root-scratch-artifact"),
            ],
            findings:
            [
                new("binary-surprise", CommitGateSeverities.Warning, "results/WEB-21/shot-01.png",
                    "Binary candidate requires explicit review.", "policy"),
                new("root-scratch-artifact", CommitGateSeverities.Warning, ".tmp-scratch.mjs",
                    "Excluded.", "policy", ExcludesCandidate: true),
            ]);

        var report = CommitWithholdingPolicy.From(gate);

        Assert.NotNull(report);
        // Only the files the commit would have contained. A deliberately
        // excluded scratch file was never going to be committed, so calling it
        // "withheld" would bury the two files that actually are.
        Assert.Equal(
            ["results/WEB-21/shot-01.png", "status.md"],
            report!.Withheld.Select(w => w.Path));
        Assert.Equal(3, report.CandidateCount);
        // A candidate with its own finding carries that code; a clean candidate
        // says honestly that the whole manifest was refused around it.
        Assert.Equal("binary-surprise", report.Withheld[0].Reason);
        Assert.Equal(CommitWithholdingReasons.WithheldWithManifest, report.Withheld[1].Reason);
        Assert.False(report.Blocked);
    }

    [Fact]
    public void From_ACommittableGateWithholdsNothing()
    {
        // Policy exclusions on a gate that CAN commit are deliberate and stay
        // excluded. Reporting them would make the marker fire on every ordinary
        // scoped commit and drown the one case that matters.
        var gate = Gate(
            CommitGateDecisions.Warn, canCommit: true,
            candidates:
            [
                Candidate("src/app.ts", included: true, size: 10),
                Candidate("debug.log", included: false, size: 10, exclusion: "root-scratch-artifact"),
            ],
            findings:
            [
                new("root-scratch-artifact", CommitGateSeverities.Warning, "debug.log",
                    "Excluded.", "policy", ExcludesCandidate: true),
            ]);

        Assert.Null(CommitWithholdingPolicy.From(gate));
    }

    [Fact]
    public void From_PrefersTheBlockingFindingAndFlagsTheReport()
    {
        var gate = Gate(
            CommitGateDecisions.Block, canCommit: false,
            candidates: [Candidate("leaked.pem", included: true, size: 64)],
            findings:
            [
                new("binary-surprise", CommitGateSeverities.Warning, "leaked.pem", "binary", "policy"),
                new("private-key-material", CommitGateSeverities.Block, "leaked.pem",
                    "Private-key material detected. The matched value was redacted.", "built-in"),
            ]);

        var report = CommitWithholdingPolicy.From(gate);

        Assert.NotNull(report);
        Assert.True(report!.Blocked);
        Assert.Equal("private-key-material", report.Withheld[0].Reason);
    }

    [Fact]
    public void From_ManifestLevelBlockNamesItselfOnEveryCandidate()
    {
        // explicit-pathspec-required / not-isolated-task-worktree are recorded
        // against "." rather than a file. Without the manifest fallback every
        // candidate would carry an empty reason.
        var gate = Gate(
            CommitGateDecisions.Block, canCommit: false,
            candidates: [Candidate("a.txt", included: true, size: 1)],
            findings:
            [
                new("explicit-pathspec-required", CommitGateSeverities.Block, ".",
                    "Explicit path set required.", "policy"),
            ]);

        Assert.Equal("explicit-pathspec-required", CommitWithholdingPolicy.From(gate)!.Withheld[0].Reason);
    }

    [Fact]
    public void ParkReason_NamesTheGateTheDecisionAndTheCount()
    {
        var report = CommitWithholdingPolicy.From(Web21Gate())!;

        var reason = CommitWithholdingPolicy.ParkReason(report);

        Assert.Contains("commit candidate gate", reason, StringComparison.Ordinal);
        Assert.Contains("(warn)", reason, StringComparison.Ordinal);
        Assert.Contains("14 of 14 candidate files", reason, StringComparison.Ordinal);
        Assert.Contains("binary-surprise", reason, StringComparison.Ordinal);
        Assert.Contains("still", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusSection_ListsWhichFilesAndWhy_AndCapsALongList()
    {
        var section = CommitWithholdingPolicy.StatusSection(
            CommitWithholdingPolicy.From(Web21Gate())!, maxRows: 3);

        Assert.Contains("Withheld by the commit candidate gate: 14", section, StringComparison.Ordinal);
        Assert.Contains("`results/WEB-21/shot-01.png` (binary-surprise)", section, StringComparison.Ordinal);
        Assert.Contains("and 11 more.", section, StringComparison.Ordinal);
    }

    // -- Park marker -------------------------------------------------------

    [Fact]
    public void Build_AnUntypedParkIsTypedByTheWithheldCommit()
    {
        // The exact WEB-21 hole: the orchestrator's no-completion-signal move
        // passes no reason at all, so the marker used to be written with an
        // empty reason and an "operator-decision" blocker type.
        var record = ParkedBlockerCatalog.Build(
            TaskStates.Escalated, reason: null, Now, CommitWithholdingPolicy.From(Web21Gate()));

        Assert.NotNull(record);
        Assert.Equal(
            HumanReviewEscalationCategories.CommitCandidatesWithheld, record!.BlockerType);
        Assert.StartsWith("[commit-candidates-withheld] ", record.Reason, StringComparison.Ordinal);
        Assert.Contains("14 of 14 candidate files", record.Reason, StringComparison.Ordinal);
        Assert.Equal(14, record.WithheldCommitCandidates.Count);
        Assert.Contains(record.WithheldCommitCandidates,
            c => c.Path == "results/WEB-21/shot-01.png" && c.Reason == "binary-surprise");
    }

    [Fact]
    public void Build_AnAlreadyTypedParkKeepsItsCategoryAndGainsTheSentence()
    {
        var record = ParkedBlockerCatalog.Build(
            TaskStates.Escalated, "[watchdog-kill] CLI exceeded the deadline", Now,
            CommitWithholdingPolicy.From(Web21Gate()));

        // The watchdog is still why the card is parked. It is not why a
        // finished delivery is uncommitted, so the card says both.
        Assert.Equal(HumanReviewEscalationCategories.WatchdogKill, record!.BlockerType);
        Assert.StartsWith("[watchdog-kill] CLI exceeded the deadline ", record.Reason, StringComparison.Ordinal);
        Assert.Contains("commit candidate gate", record.Reason, StringComparison.Ordinal);
        Assert.Equal(14, record.WithheldCommitCandidates.Count);
    }

    [Fact]
    public void Build_WithoutAWithheldCommitIsUnchanged()
    {
        var record = ParkedBlockerCatalog.Build(TaskStates.Escalated, "[watchdog-kill] died", Now);

        Assert.Equal("[watchdog-kill] died", record!.Reason);
        Assert.Empty(record.WithheldCommitCandidates);
    }

    [Fact]
    public void Build_StillProducesNoMarkerOutsideAParkedLane()
        => Assert.Null(ParkedBlockerCatalog.Build(
            TaskStates.Progress, null, Now, CommitWithholdingPolicy.From(Web21Gate())));

    [Fact]
    public void ConditionFor_TheWithheldCategoryIsAHumanDecision()
    {
        // No probe can decide "has an operator reviewed these files yet?".
        // Claiming a checkable condition would recreate the parked-card bug the
        // catalog exists to avoid.
        var condition = ParkedBlockerCatalog.ConditionFor(
            HumanReviewEscalationCategories.CommitCandidatesWithheld);
        Assert.Equal(ParkedBlockerConditionKinds.Manual, condition.Kind);
    }

    // -- Fixtures ----------------------------------------------------------

    /// <summary>The WEB-21 manifest: twelve screenshots plus two edited files,
    /// all refused because each screenshot read as a binary surprise.</summary>
    private static CommitGateResult Web21Gate()
    {
        var shots = Enumerable.Range(1, 12)
            .Select(i => $"results/WEB-21/shot-{i:00}.png")
            .ToArray();
        var candidates = shots
            .Select(p => Candidate(p, included: true, size: 40960))
            .Append(Candidate("frontend/src/app/board.ts", included: true, size: 900))
            .Append(Candidate("frontend/src/app/board.html", included: true, size: 400))
            .ToArray();
        var findings = shots
            .Select(p => new CommitGateFinding(
                "binary-surprise", CommitGateSeverities.Warning, p,
                "Binary candidate requires explicit review.", "policy"))
            .ToArray();
        return Gate(CommitGateDecisions.Warn, canCommit: false, candidates, findings);
    }

    private static CommitCandidateManifestEntry Candidate(
        string path, bool included, long size, string? exclusion = null)
        => new(path, "??", size, "sha", "oid", Binary: false, included, exclusion);

    private static CommitGateResult Gate(
        string decision,
        bool canCommit,
        IReadOnlyList<CommitCandidateManifestEntry> candidates,
        IReadOnlyList<CommitGateFinding> findings)
        => new(
            decision, canCommit,
            new CommitGateProvenance(
                "auto-commit", "Web", "WEB-21", "runner-01", "/repo", "task/WEB-21", Now),
            candidates, findings, ["built-in"]);
}
