using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2828 acceptance: "When the commit candidate gate withholds files, the
/// card shows which files and why, and the parked reason names the gate and the
/// count."
///
/// <para>WEB-21 parked with cause <c>no-completion-signal</c> and an EMPTY
/// parked reason while 12 screenshots and two edited files sat uncommitted in
/// the worktree. These rows pin the translation that makes that impossible.</para>
/// </summary>
public class WithheldCommitCandidatePolicyTests
{
    [Fact]
    public void Nothing_withheld_produces_no_record()
    {
        var gate = Gate(CommitGateDecisions.Allow, canCommit: true,
            [Entry("frontend/src/app.ts", included: true)],
            []);

        Assert.Null(WithheldCommitCandidatePolicy.Describe(gate));
        Assert.Null(WithheldCommitCandidatePolicy.Describe(null));
    }

    [Fact]
    public void Blocked_gate_withholds_every_candidate_with_its_own_finding_code()
    {
        var gate = Web21Gate();

        var record = WithheldCommitCandidatePolicy.Describe(gate);

        Assert.NotNull(record);
        Assert.Equal(14, record!.Count);
        Assert.True(record.NothingCommitted);
        Assert.Equal(CommitGateDecisions.Warn, record.Decision);
        Assert.Equal(
            "binary-surprise",
            record.Candidates.Single(c => c.Path == "results/shot-01.png").Reason);
        // A file with no finding of its own is still withheld, and says so
        // rather than carrying an empty reason.
        Assert.Equal(
            "gate-warn",
            record.Candidates.Single(c => c.Path == "status.md").Reason);
    }

    [Fact]
    public void Parked_reason_names_the_gate_and_the_count()
    {
        var record = WithheldCommitCandidatePolicy.Describe(Web21Gate());

        var reason = WithheldCommitCandidatePolicy.ComposeParkReason(
            "Run finished without a terminal sentinel.", record);

        Assert.Contains("Run finished without a terminal sentinel.", reason, StringComparison.Ordinal);
        Assert.Contains("commit candidate gate", reason, StringComparison.Ordinal);
        Assert.Contains("warn", reason, StringComparison.Ordinal);
        Assert.Contains("14 file(s)", reason, StringComparison.Ordinal);
        Assert.Contains("uncommitted", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Parked_reason_without_a_withheld_set_is_unchanged()
    {
        Assert.Equal(
            "Run finished without a terminal sentinel.",
            WithheldCommitCandidatePolicy.ComposeParkReason("Run finished without a terminal sentinel.", null));
        Assert.Equal(string.Empty, WithheldCommitCandidatePolicy.ComposeParkReason(null, null));
    }

    [Fact]
    public void Partial_withholding_does_not_claim_the_delivery_is_uncommitted()
    {
        var gate = Gate(CommitGateDecisions.Warn, canCommit: true,
            [Entry("frontend/src/app.ts", included: true),
             Entry(".tmp-helper.mjs", included: false, exclusion: "root-scratch-artifact")],
            [Finding("root-scratch-artifact", CommitGateSeverities.Warning, ".tmp-helper.mjs", excludes: true)]);

        var record = WithheldCommitCandidatePolicy.Describe(gate);
        var reason = WithheldCommitCandidatePolicy.ComposeParkReason("Parked.", record);

        Assert.Equal(1, record!.Count);
        Assert.False(record.NothingCommitted);
        Assert.Equal("root-scratch-artifact", record.Candidates[0].Reason);
        Assert.Contains("withheld 1 file(s) from the commit", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("uncommitted", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Detail_lists_which_files_and_why_plus_the_operator_action()
    {
        var record = WithheldCommitCandidatePolicy.Describe(Web21Gate());

        var detail = WithheldCommitCandidatePolicy.BuildDetail(record, "WEB-21");

        Assert.Contains("`results/shot-01.png` - binary-surprise", detail, StringComparison.Ordinal);
        Assert.Contains("`docs/report.md`", detail, StringComparison.Ordinal);
        Assert.Contains("/api/tasks/WEB-21/git/withheld-candidates/commit", detail, StringComparison.Ordinal);
        // The stub parser lifts exactly the Category / Reason lines back out, so
        // the detail must never introduce a second one of either shape.
        Assert.DoesNotContain("- Category:", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("- Reason:", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Detail_summarizes_the_tail_instead_of_rendering_every_path()
    {
        var candidates = Enumerable.Range(0, WithheldCommitCandidatePolicy.MaxRenderedPaths + 5)
            .Select(i => Entry($"results/shot-{i:00}.png", included: false, exclusion: null, binary: true))
            .ToArray();
        var findings = candidates
            .Select(c => Finding("binary-surprise", CommitGateSeverities.Warning, c.Path))
            .ToArray();

        var detail = WithheldCommitCandidatePolicy.BuildDetail(
            WithheldCommitCandidatePolicy.Describe(
                Gate(CommitGateDecisions.Warn, canCommit: false, candidates, findings)),
            "WEB-21");

        Assert.Contains("and 5 more", detail, StringComparison.Ordinal);
        Assert.Contains(WithheldCommitCandidateStore.FileName, detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Remaining_keeps_what_did_not_land_and_clears_once_everything_did()
    {
        var record = WithheldCommitCandidatePolicy.Describe(Web21Gate())!;

        var partial = WithheldCommitCandidatePolicy.Remaining(
            record, ["docs/report.md", "status.md"]);

        Assert.Equal(12, partial!.Count);
        Assert.False(partial.NothingCommitted);
        Assert.DoesNotContain(partial.Candidates, c => c.Path == "docs/report.md");

        Assert.Null(WithheldCommitCandidatePolicy.Remaining(
            record, record.Candidates.Select(c => c.Path).ToArray()));
        // An unrelated path landing changes nothing about what is still waiting.
        Assert.Equal(record.Count,
            WithheldCommitCandidatePolicy.Remaining(record, ["other.txt"])!.Count);
        Assert.Null(WithheldCommitCandidatePolicy.Remaining(null, ["docs/report.md"]));
    }

    [Fact]
    public void Repository_scoped_findings_explain_candidates_that_have_none_of_their_own()
    {
        var gate = Gate(CommitGateDecisions.Block, canCommit: false,
            [Entry("frontend/src/app.ts", included: true)],
            [Finding("not-isolated-task-worktree", CommitGateSeverities.Block, ".")]);

        var record = WithheldCommitCandidatePolicy.Describe(gate);

        Assert.Equal("not-isolated-task-worktree", record!.Candidates[0].Reason);
    }

    [Fact]
    public void Informational_findings_never_become_a_withholding_reason()
    {
        var gate = Gate(CommitGateDecisions.Block, canCommit: false,
            [Entry("docs/assets/board.png", included: true, exclusion: null, binary: true),
             Entry("secret.pem", included: true)],
            [Finding(EvidenceAssetCodes.Admitted, CommitGateSeverities.Info, "docs/assets/board.png"),
             Finding("private-key-material", CommitGateSeverities.Block, "secret.pem")]);

        var record = WithheldCommitCandidatePolicy.Describe(gate);

        // The admitted asset is held back only because the gate refused as a
        // whole. Reporting its own admission code as the withholding reason
        // would read as "this screenshot is the problem", which is backwards.
        Assert.Equal(
            "gate-block",
            record!.Candidates.Single(c => c.Path == "docs/assets/board.png").Reason);
        Assert.Equal(
            "private-key-material",
            record.Candidates.Single(c => c.Path == "secret.pem").Reason);
        Assert.DoesNotContain(record.Candidates, c =>
            c.Reason.Contains(EvidenceAssetCodes.Admitted, StringComparison.Ordinal));
    }

    /// <summary>The WEB-21 shape: 12 screenshots plus two edited files, gate
    /// warns on every binary, nothing is committed.</summary>
    private static CommitGateResult Web21Gate()
    {
        var shots = Enumerable.Range(1, 12)
            .Select(i => Entry($"results/shot-{i:00}.png", included: true, exclusion: null, binary: true))
            .ToArray();
        var candidates = shots
            .Concat([Entry("docs/report.md", included: true), Entry("status.md", included: true)])
            .ToArray();
        var findings = shots
            .Select(shot => Finding("binary-surprise", CommitGateSeverities.Warning, shot.Path))
            .ToArray();
        return Gate(CommitGateDecisions.Warn, canCommit: false, candidates, findings);
    }

    private static CommitGateResult Gate(
        string decision,
        bool canCommit,
        IReadOnlyList<CommitCandidateManifestEntry> candidates,
        IReadOnlyList<CommitGateFinding> findings) =>
        new(decision, canCommit,
            new CommitGateProvenance(
                "worktree-run", "Web", "WEB-21", "runner-1", "/repo", "task/WEB-21",
                new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc)),
            candidates, findings, ["built-in"]);

    private static CommitCandidateManifestEntry Entry(
        string path, bool included, string? exclusion = null, bool binary = false) =>
        new(path, "??", 1024, "sha", "oid", binary, included, exclusion);

    private static CommitGateFinding Finding(
        string code, string severity, string path, bool excludes = false) =>
        new(code, severity, path, "message", "policy", excludes);
}
