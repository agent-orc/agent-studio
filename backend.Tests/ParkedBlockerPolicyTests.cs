using System.Diagnostics;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix over the pure halves of the parked-card Wiedervorlage
/// (AGT-2492): which condition a park category waits on
/// (<see cref="ParkedBlockerCatalog"/>), what a probe verdict means
/// (<see cref="ParkedCardRecallPolicy"/>), and what the built-in
/// <see cref="ParkedBlockerProbe"/> actually decides against a real repository.
/// </summary>
public sealed class ParkedBlockerPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    // -- Catalog -----------------------------------------------------------

    [Theory]
    // The AGT-2220 class: a baseline-comparison review cannot materialize its
    // subject until the card branch carries the integration branch. That IS a
    // checkable fact, so it must not be filed as a manual decision.
    [InlineData(HumanReviewEscalationCategories.ReviewSubjectUnmaterializable, ParkedBlockerConditionKinds.GitAncestor)]
    // Everything else is a person's call or a spent budget. Claiming a
    // checkable condition here would recreate the bug: a card that looks
    // handled and is not.
    [InlineData(HumanReviewEscalationCategories.WorktreeBlocked, ParkedBlockerConditionKinds.Manual)]
    [InlineData(HumanReviewEscalationCategories.HumanDecisionNeeded, ParkedBlockerConditionKinds.Manual)]
    [InlineData(HumanReviewEscalationCategories.Quarantined, ParkedBlockerConditionKinds.Manual)]
    [InlineData(HumanReviewEscalationCategories.UnknownLegacy, ParkedBlockerConditionKinds.Manual)]
    [InlineData(ParkedBlockerCatalog.OperatorDecision, ParkedBlockerConditionKinds.Manual)]
    public void ConditionFor_MapsCategoryToItsCheckableCondition(string category, string expectedKind)
    {
        var condition = ParkedBlockerCatalog.ConditionFor(category);
        Assert.Equal(expectedKind, condition.Kind);
        Assert.False(string.IsNullOrWhiteSpace(condition.Description));
    }

    [Theory]
    [InlineData("[review-subject-unmaterialisierbar] budget exhausted", "review-subject-unmaterialisierbar")]
    [InlineData("[watchdog-kill]", "watchdog-kill")]
    // An operator move carries prose, not a category. Guessing one would put a
    // wrong blocker type on the card.
    [InlineData("Parked for Robert to decide", ParkedBlockerCatalog.OperatorDecision)]
    [InlineData("[] empty category", ParkedBlockerCatalog.OperatorDecision)]
    [InlineData(null, ParkedBlockerCatalog.OperatorDecision)]
    public void ReadBlockerType_RecoversTheCategoryFromAFormattedReason(string? reason, string expected)
        => Assert.Equal(expected, ParkedBlockerCatalog.ReadBlockerType(reason));

    [Theory]
    [InlineData(TaskStates.HumanReview, true)]
    [InlineData(TaskStates.Escalated, true)]
    [InlineData(TaskStates.Ready, false)]
    [InlineData(TaskStates.AutoReview, false)]
    [InlineData(TaskStates.Completed, false)]
    public void Build_OnlyProducesAMarkerForAParkedLane(string lane, bool expected)
        => Assert.Equal(expected, ParkedBlockerCatalog.Build(lane, "[watchdog-kill] died", Now) is not null);

    // AGT-2838: a park must never carry an empty reason. A caller that only
    // knows the lane-change taxonomy (cause + qualifier), not free-form prose,
    // must still leave a readable cause on the card.
    [Theory]
    [InlineData(null, null, null, "Parked with no recorded cause.")]
    [InlineData(null, "escalated", null, "escalated")]
    [InlineData(null, null, "no-completion-signal", "no-completion-signal")]
    [InlineData(null, "escalated", "no-completion-signal", "escalated: no-completion-signal")]
    [InlineData("", "review-verdict", "gate-failure", "review-verdict: gate-failure")]
    [InlineData("   ", "review-verdict", "gate-failure", "review-verdict: gate-failure")]
    [InlineData("[no-completion-signal] agent did not signal", "escalated", "no-completion-signal", "[no-completion-signal] agent did not signal")]
    public void Build_NeverProducesAnEmptyReason(
        string? reason, string? transitionCause, string? transitionDetail, string expectedReason)
    {
        var record = ParkedBlockerCatalog.Build(
            TaskStates.HumanReview, reason, Now, transitionCause, transitionDetail);

        Assert.NotNull(record);
        Assert.False(string.IsNullOrWhiteSpace(record!.Reason));
        Assert.Equal(expectedReason, record.Reason);
    }

    // -- Recall policy -----------------------------------------------------

    [Theory]
    [InlineData(ParkedBlockerStatuses.Recallable, true)]
    [InlineData(ParkedBlockerStatuses.Blocked, false)]
    [InlineData(ParkedBlockerStatuses.Undeterminable, false)]
    public void Decide_OnlyAResolvedConditionMakesACardRecallable(string status, bool recallable)
    {
        var recall = ParkedCardRecallPolicy.Decide(
            Candidate(Now.AddDays(-3)), Record(Now.AddDays(-3)), Evaluation(status), Now);

        Assert.Equal(recallable, recall.IsRecallable);
        Assert.Equal(3 * 86400, recall.ParkedForSeconds);
    }

    [Fact]
    public void Decide_FallsBackToLaneEntryWhenTheMarkerHasNoParkTimestamp()
    {
        // Legacy cards get their marker backfilled by the sweep; their age has
        // to come from the lane-entry stamp or the aging column reads zero on
        // exactly the cards that sat longest.
        var recall = ParkedCardRecallPolicy.Decide(
            Candidate(Now.AddDays(-6)),
            Record(default),
            Evaluation(ParkedBlockerStatuses.Undeterminable),
            Now);

        Assert.Equal(6 * 86400, recall.ParkedForSeconds);
    }

    [Fact]
    public void ShouldAnnounce_OnlyOncePerResolution_AndAgainAfterAReBlock()
    {
        var record = Record(Now.AddDays(-1));
        var resolved = Evaluation(ParkedBlockerStatuses.Recallable);

        Assert.True(ParkedCardRecallPolicy.ShouldAnnounce(record, resolved));

        var afterAnnouncement = ParkedCardRecallPolicy.Fold(record, resolved, announced: true, Now);
        Assert.Equal(Now, afterAnnouncement.ReportedRecallableAt);
        Assert.False(ParkedCardRecallPolicy.ShouldAnnounce(afterAnnouncement, resolved));

        // A condition that comes back (a branch reset, a revert) clears the
        // announcement so its next resolution is reported again.
        var reBlocked = ParkedCardRecallPolicy.Fold(
            afterAnnouncement, Evaluation(ParkedBlockerStatuses.Blocked), announced: false, Now);
        Assert.Null(reBlocked.ReportedRecallableAt);
        Assert.True(ParkedCardRecallPolicy.ShouldAnnounce(reBlocked, resolved));
    }

    [Fact]
    public void NeedsPersist_OnlyOnARealChange()
    {
        var blocked = ParkedCardRecallPolicy.Fold(
            Record(Now), Evaluation(ParkedBlockerStatuses.Blocked), announced: false, Now);

        // Backfill of a card that had no marker.
        Assert.True(ParkedCardRecallPolicy.NeedsPersist(null, blocked));

        // Same verdict an hour later: no write, or the card's activity age
        // resets every tick.
        var unchanged = ParkedCardRecallPolicy.Fold(
            blocked, Evaluation(ParkedBlockerStatuses.Blocked), announced: false, Now.AddHours(1));
        Assert.False(ParkedCardRecallPolicy.NeedsPersist(blocked, unchanged));

        // The blocker clearing is a real change.
        var resolved = ParkedCardRecallPolicy.Fold(
            blocked, Evaluation(ParkedBlockerStatuses.Recallable), announced: true, Now.AddHours(1));
        Assert.True(ParkedCardRecallPolicy.NeedsPersist(blocked, resolved));
    }

    // -- Built-in probe ----------------------------------------------------

    [Theory]
    [InlineData(ParkedBlockerConditionKinds.Manual)]
    [InlineData("some-future-kind-no-probe-knows")]
    public void Probe_WithoutAnAutomaticCondition_IsUndeterminableNotRecallable(string kind)
    {
        var verdict = new ParkedBlockerProbe().Evaluate(
            new ParkedBlockerCondition { Kind = kind }, new ParkedBlockerContext(), Now);

        Assert.Equal(ParkedBlockerStatuses.Undeterminable, verdict.Status);
    }

    [Fact]
    public void Probe_GitAncestorWithoutBranches_IsUndeterminableNotRecallable()
    {
        // Fail-safe direction: an operator acts on "recallable", so a probe that
        // cannot read the facts must never produce it.
        var verdict = BuildGitProbe(repoRoot: null).Evaluate(
            new ParkedBlockerCondition { Kind = ParkedBlockerConditionKinds.GitAncestor },
            new ParkedBlockerContext(RepositoryRoot: "/nowhere"),
            Now);

        Assert.Equal(ParkedBlockerStatuses.Undeterminable, verdict.Status);
    }

    [Fact]
    public void Probe_GitAncestor_FlipsToRecallableOnceTheCardBranchCarriesIntegration()
    {
        // The AGT-2220 remedy, reproduced: the card branch predates develop, so
        // no review baseline can be materialized. Merging develop into the card
        // branch is what actually happened on 2026-08-02 - and it is precisely
        // the moment the sweep must notice.
        var repo = InitRepository();
        try
        {
            var probe = BuildGitProbe(repo);
            var condition = new ParkedBlockerCondition { Kind = ParkedBlockerConditionKinds.GitAncestor };
            var context = new ParkedBlockerContext(repo, TaskBranch: "task/agt-2220", IntegrationBranch: "develop");

            Assert.Equal(ParkedBlockerStatuses.Blocked, probe.Evaluate(condition, context, Now).Status);

            Git(repo, "checkout", "task/agt-2220");
            Git(repo, "merge", "--no-edit", "develop");

            var after = probe.Evaluate(condition, context, Now);
            Assert.Equal(ParkedBlockerStatuses.Recallable, after.Status);
            Assert.Contains("develop", after.Detail);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best-effort */ }
        }
    }

    // -- helpers -----------------------------------------------------------

    private static ParkedCardCandidate Candidate(DateTime enteredLaneAt)
        => new("demo", "card", "demo/card", "Card", TaskStates.HumanReview, enteredLaneAt);

    private static ParkedBlockerRecord Record(DateTime parkedAt) => new()
    {
        BlockerType = HumanReviewEscalationCategories.ReviewSubjectUnmaterializable,
        Condition = ParkedBlockerCatalog.ConditionFor(HumanReviewEscalationCategories.ReviewSubjectUnmaterializable),
        Lane = TaskStates.HumanReview,
        ParkedAt = parkedAt,
        Reason = "4x ReviewInfra/BaselineUnavailable",
    };

    private static ParkedBlockerEvaluation Evaluation(string status)
        => new() { Status = status, At = Now, Detail = status };

    private static ParkedBlockerProbe BuildGitProbe(string? repoRoot)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "demo",
                ["WatchPaths:0:Path"] = repoRoot ?? Path.GetTempPath(),
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        return new ParkedBlockerProbe(
            new GitService(NullLogger<GitService>.Instance, scanner, config, prompts));
    }

    /// <summary>A repository whose <c>task/agt-2220</c> branch was cut before
    /// <c>develop</c> moved on - the state the parked card was in.</summary>
    private static string InitRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "atp-parked-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Git(root, "init", "--initial-branch=develop");
        Git(root, "config", "user.email", "test@example.com");
        Git(root, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(root, "README.md"), "base\n");
        Git(root, "add", ".");
        Git(root, "commit", "-m", "base");

        Git(root, "checkout", "-b", "task/agt-2220");
        File.WriteAllText(Path.Combine(root, "card.txt"), "card work\n");
        Git(root, "add", ".");
        Git(root, "commit", "-m", "card work");

        Git(root, "checkout", "develop");
        File.WriteAllText(Path.Combine(root, "moved-on.txt"), "develop moved on\n");
        Git(root, "add", ".");
        Git(root, "commit", "-m", "develop moved on");
        return root;
    }

    private static void Git(string root, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0,
            $"git {string.Join(' ', args)} failed: {process.StandardError.ReadToEnd()}");
    }
}
