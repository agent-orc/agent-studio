using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix for the stale-branch sweep classification (AGT-2794). No git,
/// no filesystem, no clock: the sweep contributes facts, the shared
/// <see cref="BranchRetentionPolicy"/> decides, and this pins the result for
/// every namespace including orphans, protected refs, and unmanaged refs.
/// </summary>
public class BranchSweepPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-14T12:00:00Z");
    private static readonly BranchRetentionWindows Windows = BranchRetentionWindows.Default;

    private static BranchSweepObservation Observe(
        string branch,
        int ageDays = 400,
        bool inMain = false,
        bool inDevelop = false,
        bool checkedOut = false,
        string? taskState = null,
        bool referenced = false,
        bool mainAvailable = true,
        bool developAvailable = true)
        => new(
            branch,
            "0123456789abcdef0123456789abcdef01234567",
            "0123456",
            Now.AddDays(-ageDays),
            inMain,
            inDevelop,
            mainAvailable,
            developAvailable,
            checkedOut,
            BranchSweepPolicy.DeriveTaskKey(branch),
            taskState,
            referenced);

    private static BranchSweepCandidate Classify(BranchSweepObservation observation)
        => BranchSweepPolicy.Classify(observation, Now, Windows);

    // ---- classes -----------------------------------------------------------

    [Theory]
    [InlineData("task/AGT-1234", BranchSweepClasses.Task)]
    [InlineData("runner/agent-runner-01/AGT-1234", BranchSweepClasses.Runner)]
    [InlineData("delivery/AGT-1234", BranchSweepClasses.Delivery)]
    [InlineData("agent-studio/results/attempt-1/fence-1/abc123", BranchSweepClasses.Results)]
    [InlineData("agent-studio/salvage/runner-1/AGT-1234/attempt-1/fence-1/abc123", BranchSweepClasses.Salvage)]
    [InlineData("agent-studio/quarantine/runner-1/AGT-1234/attempt-1/fence-1/abc123", BranchSweepClasses.Quarantine)]
    [InlineData("main", BranchSweepClasses.Protected)]
    [InlineData("develop", BranchSweepClasses.Protected)]
    [InlineData("release/1.4", BranchSweepClasses.Protected)]
    [InlineData("v1.4.0", BranchSweepClasses.Protected)]
    [InlineData("feature/some-experiment", BranchSweepClasses.Unmanaged)]
    public void EveryNamespaceGetsItsReportClass(string branch, string expected)
        => Assert.Equal(expected, Classify(Observe(branch)).Class);

    [Theory]
    [InlineData("task/AGT-1234", "AGT-1234")]
    [InlineData("delivery/AGT-1234", "AGT-1234")]
    [InlineData("runner/agent-runner-01/AGT-1234", "AGT-1234")]
    [InlineData("agent-studio/salvage/runner-1/AGT-1234/attempt-1/fence-1/abc", "AGT-1234")]
    [InlineData("agent-studio/quarantine/runner-1/AGT-1234/attempt-1/fence-1/abc", "AGT-1234")]
    [InlineData("agent-studio/results/attempt-1/fence-1/abc", null)]
    [InlineData("feature/experiment", null)]
    [InlineData("task/", null)]
    public void TaskKeyIsDerivedFromTheRefLayout(string branch, string? expected)
        => Assert.Equal(expected, BranchSweepPolicy.DeriveTaskKey(branch));

    // ---- never candidates --------------------------------------------------

    [Theory]
    [InlineData("main")]
    [InlineData("develop")]
    [InlineData("release/1.4")]
    [InlineData("v2.0.0")]
    [InlineData("feature/some-experiment")]
    public void ProtectedAndUnmanagedRefsAreNeverCandidates(string branch)
    {
        var candidate = Classify(Observe(branch, ageDays: 5000, inMain: true, inDevelop: true));

        Assert.False(candidate.Eligible);
        Assert.Equal(nameof(BranchRetentionDecision.UnsupportedNamespace), candidate.Decision);
    }

    [Fact]
    public void CheckedOutRefIsNeverACandidate()
    {
        var candidate = Classify(Observe(
            "task/AGT-1234", inMain: true, inDevelop: true, checkedOut: true, taskState: TaskStates.Archive));

        Assert.False(candidate.Eligible);
        Assert.Equal(nameof(BranchRetentionDecision.CheckedOut), candidate.Decision);
    }

    // ---- task / runner / delivery -----------------------------------------

    [Theory]
    [InlineData("task/AGT-1234")]
    [InlineData("runner/agent-runner-01/AGT-1234")]
    [InlineData("delivery/AGT-1234")]
    public void MergedAndAgedTaskRefIsEligible(string branch)
    {
        var candidate = Classify(Observe(
            branch, ageDays: 30, inMain: true, inDevelop: true, taskState: TaskStates.Completed));

        Assert.True(candidate.Eligible);
        Assert.Equal(nameof(BranchRetentionDecision.Delete), candidate.Decision);
    }

    [Fact]
    public void MergedButYoungerThanTheWindowIsKept()
    {
        var candidate = Classify(Observe(
            "task/AGT-1234", ageDays: 2, inMain: true, inDevelop: true, taskState: TaskStates.Completed));

        Assert.False(candidate.Eligible);
        Assert.Equal(nameof(BranchRetentionDecision.TooYoung), candidate.Decision);
    }

    [Fact]
    public void UnmergedRefOfALiveCardIsKeptHoweverOld()
    {
        var candidate = Classify(Observe("task/AGT-1234", ageDays: 900, taskState: TaskStates.Progress));

        Assert.False(candidate.Eligible);
        Assert.Equal(nameof(BranchRetentionDecision.NotMergedIntoDevelop), candidate.Decision);
    }

    [Fact]
    public void UnmergedRefOfAnArchivedCardIsKeptUntilTheAbandonedWindowPasses()
    {
        var candidate = Classify(Observe(
            "task/AGT-1234",
            ageDays: BranchRetentionWindows.DefaultAbandonedDays - 1,
            taskState: TaskStates.Archive));

        Assert.False(candidate.Eligible);
        Assert.Equal(nameof(BranchRetentionDecision.NotMergedIntoDevelop), candidate.Decision);
    }

    [Fact]
    public void UnmergedRefOfAnArchivedCardIsReclaimedAfterTheAbandonedWindow()
    {
        var candidate = Classify(Observe(
            "task/AGT-1234",
            ageDays: BranchRetentionWindows.DefaultAbandonedDays + 1,
            taskState: TaskStates.Archive));

        Assert.True(candidate.Eligible);
        Assert.Equal(nameof(BranchRetentionDecision.AbandonedRefAged), candidate.Decision);
    }

    /// <summary>Task key unknown = no card at all; the class age rule decides.</summary>
    [Fact]
    public void OrphanTaskRefFollowsTheClassAgeRule()
    {
        var young = Classify(Observe("task/AGT-9999", ageDays: 10, taskState: null));
        var old = Classify(Observe("task/AGT-9999", ageDays: 500, taskState: null));

        Assert.False(young.Eligible);
        Assert.Null(young.TaskState);
        Assert.True(old.Eligible);
        Assert.Equal(nameof(BranchRetentionDecision.AbandonedRefAged), old.Decision);
    }

    [Fact]
    public void RefReferencedByAnOpenCardIsNeverAbandoned()
    {
        var candidate = Classify(Observe("task/AGT-1234", ageDays: 900, taskState: null, referenced: true));

        Assert.False(candidate.Eligible);
        Assert.Equal(nameof(BranchRetentionDecision.TaskRefReferenced), candidate.Decision);
    }

    [Fact]
    public void MissingProtectedTipsRetainEverything()
    {
        var candidate = Classify(Observe(
            "task/AGT-1234", ageDays: 500, taskState: TaskStates.Archive, developAvailable: false));

        Assert.False(candidate.Eligible);
        Assert.Equal(nameof(BranchRetentionDecision.DevelopUnavailable), candidate.Decision);
    }

    // ---- results / salvage / quarantine -----------------------------------

    [Fact]
    public void ResultRefIsEligibleOnlyOnceItsProofIsInMain()
    {
        const string reference = "agent-studio/results/attempt-1/fence-1/abc123";

        Assert.False(Classify(Observe(reference, ageDays: 900)).Eligible);
        Assert.True(Classify(Observe(reference, ageDays: 0, inMain: true)).Eligible);
    }

    /// <summary>
    /// A result ref carries no task key at all, so "orphan" cannot apply to it:
    /// it stays bound to main containment and is never dropped on age alone.
    /// </summary>
    [Fact]
    public void OrphanResultRefIsNotDroppedByAge()
    {
        var candidate = Classify(Observe(
            "agent-studio/results/attempt-1/fence-1/abc123", ageDays: 5000, taskState: null));

        Assert.False(candidate.Eligible);
        Assert.Equal(nameof(BranchRetentionDecision.ResultRefNotInMain), candidate.Decision);
    }

    [Fact]
    public void SalvageRefIsEligibleOnceItsWindowPasses()
    {
        const string reference = "agent-studio/salvage/runner-1/AGT-1234/attempt-1/fence-1/abc";

        Assert.False(Classify(Observe(reference, ageDays: 3, taskState: TaskStates.Progress)).Eligible);
        Assert.True(Classify(Observe(
            reference,
            ageDays: BranchRetentionWindows.DefaultSalvageDays + 1,
            taskState: TaskStates.Archive)).Eligible);
    }

    [Fact]
    public void SalvageRefReferencedByAnOpenCardSurvivesItsWindow()
    {
        var candidate = Classify(Observe(
            "agent-studio/salvage/runner-1/AGT-1234/attempt-1/fence-1/abc",
            ageDays: 900,
            taskState: TaskStates.Progress,
            referenced: true));

        Assert.False(candidate.Eligible);
        Assert.Equal(nameof(BranchRetentionDecision.TaskRefReferenced), candidate.Decision);
    }

    [Fact]
    public void QuarantineRefIsEligibleOnlyAfterItsWindowAndNeverWhileReferenced()
    {
        const string reference = "agent-studio/quarantine/runner-1/AGT-1234/attempt-1/fence-1/abc";

        Assert.False(Classify(Observe(reference, ageDays: 10)).Eligible);
        Assert.True(Classify(Observe(
            reference, ageDays: BranchRetentionWindows.DefaultQuarantineDays + 1)).Eligible);

        var referenced = Classify(Observe(reference, ageDays: 900, referenced: true));
        Assert.False(referenced.Eligible);
        Assert.Equal(nameof(BranchRetentionDecision.QuarantineRefReferenced), referenced.Decision);
    }

    // ---- per-project window overrides -------------------------------------

    [Fact]
    public void ProjectWindowOverridesShiftTheSameDecisionMatrix()
    {
        var observation = Observe(
            "agent-studio/quarantine/runner-1/AGT-1234/attempt-1/fence-1/abc", ageDays: 10);

        Assert.False(BranchSweepPolicy.Classify(observation, Now, Windows).Eligible);
        Assert.True(BranchSweepPolicy
            .Classify(observation, Now, Windows with { QuarantineDays = 5 })
            .Eligible);
    }

    [Fact]
    public void WindowOverridesAreClampedIntoARealRange()
    {
        var clamped = new BranchRetentionWindows(0, -5, 99999, 0).Clamped();

        Assert.Equal(1, clamped.TaskDays);
        Assert.Equal(1, clamped.SalvageDays);
        Assert.Equal(3650, clamped.QuarantineDays);
        Assert.Equal(1, clamped.AbandonedDays);
    }

    // ---- report aggregation -----------------------------------------------

    [Theory]
    [InlineData(0, "0-6 days")]
    [InlineData(6, "0-6 days")]
    [InlineData(7, "7-29 days")]
    [InlineData(89, "30-89 days")]
    [InlineData(364, "90-364 days")]
    [InlineData(4000, "365+ days")]
    [InlineData(null, "unknown age")]
    public void AgeBucketsCoverTheWholeRange(int? ageDays, string expected)
        => Assert.Equal(expected, BranchSweepPolicy.AgeBucketOf(ageDays));

    [Fact]
    public void TotalsAndHistogramSummarizeTheCandidateList()
    {
        var candidates = new List<BranchSweepCandidate>
        {
            Classify(Observe("task/AGT-1", ageDays: 30, inMain: true, inDevelop: true, taskState: TaskStates.Completed)),
            Classify(Observe("task/AGT-2", ageDays: 2, taskState: TaskStates.Progress)),
            Classify(Observe("agent-studio/results/a/fence-1/abc", ageDays: 400, inMain: true)),
        };
        var deletions = new List<BranchSweepDeletion>
        {
            new("task/AGT-1", BranchSweepClasses.Task, "sha", true, "Deleted."),
        };

        var totals = BranchSweepReportBuilder.Totals(candidates, deletions);
        var task = Assert.Single(totals, t => t.Class == BranchSweepClasses.Task);

        Assert.Equal(2, task.Total);
        Assert.Equal(1, task.Eligible);
        Assert.Equal(1, task.Deleted);
        Assert.Equal(1, task.Kept);
        // Aggregate = sum of the visible children in every column.
        Assert.Equal(candidates.Count, totals.Sum(t => t.Total));
        Assert.Equal(
            candidates.Count,
            BranchSweepReportBuilder.AgeHistogram(candidates).Sum(bucket => bucket.Refs));
    }

    [Fact]
    public void MarkdownSummaryCarriesModeTotalsAndDeletions()
    {
        var candidates = new List<BranchSweepCandidate>
        {
            Classify(Observe("task/AGT-1", ageDays: 30, inMain: true, inDevelop: true, taskState: TaskStates.Completed)),
        };
        var deletions = new List<BranchSweepDeletion>
        {
            new("task/AGT-1", BranchSweepClasses.Task, "0123456789abcdef", true, "Deleted."),
        };
        var report = new BranchSweepReport(
            "Demo", "/repo", BranchSweepModes.Reclaim, Now, Now, Windows, 1, 0,
            BranchSweepReportBuilder.Totals(candidates, deletions),
            BranchSweepReportBuilder.AgeHistogram(candidates),
            candidates, deletions, null);

        var markdown = BranchSweepReportBuilder.RenderMarkdown(report);

        Assert.Contains("# Branch sweep - Demo - 20260914T120000Z", markdown, StringComparison.Ordinal);
        Assert.Contains("Mode: `reclaim`", markdown, StringComparison.Ordinal);
        Assert.Contains("| task | 1 | 1 | 0 | 1 |", markdown, StringComparison.Ordinal);
        Assert.Contains("`task/AGT-1`", markdown, StringComparison.Ordinal);
    }

    // ---- push status parsing ----------------------------------------------

    [Fact]
    public void PorcelainPushStatusesSeparateDeletedFromRejected()
    {
        const string output =
            "To /tmp/bare.git\n"
            + "-\t:refs/heads/task/AGT-1\t[deleted]\n"
            + "!\t:refs/heads/task/AGT-2\t[remote rejected] (stale info)\n"
            + "Done\n";

        var statuses = GitService.ParsePorcelainPushStatuses(output);

        Assert.True(statuses["refs/heads/task/AGT-1"].Ok);
        Assert.False(statuses["refs/heads/task/AGT-2"].Ok);
        Assert.Contains("stale info", statuses["refs/heads/task/AGT-2"].Detail, StringComparison.Ordinal);
    }
}

/// <summary>
/// The report store is the only thing between a sweep run and the operator UI,
/// so the JSON it writes has to come back as the same record.
/// </summary>
public sealed class BranchSweepReportStoreTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "branch-sweep-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort */ }
    }

    private BranchSweepReportStore Store()
        => new(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
                .Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BranchSweepReportStore>.Instance);

    private static BranchSweepReport Sample(DateTimeOffset at) => new(
        "Demo", "/repo", BranchSweepModes.Reclaim, at, at,
        BranchRetentionWindows.Default with { QuarantineDays = 45 },
        3, 2,
        [new BranchSweepClassTotals(BranchSweepClasses.Task, 3, 1, 2, 1)],
        [new BranchSweepAgeBucket("365+ days", 3)],
        [new BranchSweepCandidate(
            "task/DEM-1", BranchSweepClasses.Task, "DEM-1", TaskStates.Archive,
            "a".PadRight(40, 'a'), "aaaaaaa", at.AddDays(-400), 400, true, true, false,
            nameof(BranchRetentionDecision.Delete), true, "Eligible for deletion; deletion policy met.")],
        [new BranchSweepDeletion("task/DEM-1", BranchSweepClasses.Task, "a".PadRight(40, 'a'), true, "Deleted.")],
        null);

    [Fact]
    public void WrittenReportRoundTripsThroughTheStore()
    {
        var store = Store();
        var report = Sample(DateTimeOffset.Parse("2026-09-14T12:00:00Z"));

        var path = store.Write(report);

        Assert.NotNull(path);
        Assert.True(File.Exists(Path.ChangeExtension(path, ".md")));
        var restored = store.Latest("Demo");
        Assert.NotNull(restored);
        // Records compare their list members by reference, so the collections
        // are checked element-wise.
        Assert.Equal(report with { Totals = [], AgeHistogram = [], Candidates = [], Deletions = [] },
            restored with { Totals = [], AgeHistogram = [], Candidates = [], Deletions = [] });
        Assert.Equal(report.Totals, restored.Totals);
        Assert.Equal(report.AgeHistogram, restored.AgeHistogram);
        Assert.Equal(report.Candidates, restored.Candidates);
        Assert.Equal(report.Deletions, restored.Deletions);
    }

    [Fact]
    public void LatestPicksTheNewestRunAndHistoryListsThemNewestFirst()
    {
        var store = Store();
        store.Write(Sample(DateTimeOffset.Parse("2026-09-13T09:00:00Z")));
        var newest = Sample(DateTimeOffset.Parse("2026-09-14T12:00:00Z")) with { RefsAfter = 1 };
        store.Write(newest);

        Assert.Equal(1, store.Latest("Demo")!.RefsAfter);
        Assert.Equal(
            ["20260914T120000Z", "20260913T090000Z"],
            store.History("Demo"));
    }

    [Fact]
    public void WithoutAWorkspaceRootNothingIsWrittenAndNothingIsRead()
    {
        var store = new BranchSweepReportStore(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BranchSweepReportStore>.Instance);

        Assert.Null(store.Write(Sample(DateTimeOffset.UtcNow)));
        Assert.Null(store.Latest("Demo"));
        Assert.Empty(store.History("Demo"));
    }
}
