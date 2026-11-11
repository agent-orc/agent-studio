using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3483: when a card counts as a member of a batch under isolation, as a
/// pure matrix and read from a real batch gate store.
/// </summary>
public sealed class BatchIsolationGuardTests : IDisposable
{
    private const string Project = "demo";
    private const string TaskKey = "DEMO-7";
    private const string ReviewAttempt = "rev-demo-7";
    private static readonly string Base = new('a', 40);
    private static readonly string Result = new('b', 40);
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly BatchGateScope Scope = new(Project, "repo", "develop", "full", "digest", "v1");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "batch-isolation-guard-" + Guid.NewGuid().ToString("N"));
    private readonly BatchGateStore _store;
    private readonly string _folder;

    public BatchIsolationGuardTests()
    {
        _store = new BatchGateStore(Path.Combine(_root, "batch-gates"));
        _folder = Path.Combine(_root, "projects", Project, TaskStates.AutoReview, TaskKey);
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "test teardown is best-effort"); }
    }

    [Theory]
    [InlineData("batch-1", true, BatchPhase.Red, false, true)]
    [InlineData(null, true, BatchPhase.Red, false, false)]
    [InlineData("batch-1", false, BatchPhase.Red, false, false)]
    [InlineData("batch-1", true, BatchPhase.Running, false, false)]
    [InlineData("batch-1", true, BatchPhase.Paused, false, false)]
    [InlineData("batch-1", true, BatchPhase.Published, false, false)]
    [InlineData("batch-1", true, BatchPhase.Red, true, false)]
    public void Isolation_holds_only_for_an_active_member_of_a_red_batch_that_has_not_released_it(
        string? batchId, bool pendingActive, BatchPhase phase, bool memberLeft, bool expected)
        => Assert.Equal(expected, BatchIsolationGuardPolicy.IsIsolating(batchId, pendingActive, phase, memberLeft, true));

    [Fact]
    public void A_red_parent_without_a_live_flight_does_not_refuse_a_requeue()
        => Assert.False(BatchIsolationGuardPolicy.IsIsolating("batch-1", true, BatchPhase.Red, false));

    [Fact]
    public void A_live_flight_refuses_a_red_parent_member()
    {
        var manifest = SeedMember();
        BatchGateStates.Reach(_store, new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
            BatchPhase.Red, Result, 1, Now, "deterministic suite red"));
        var membership = new BatchIsolationGuard(_store, activeFlight: _ => true).Find(Card());
        Assert.Equal(new BatchIsolationMembership(Project, TaskKey, manifest.BatchId), membership);
    }

    [Theory]
    [InlineData(TaskStates.AutoReview, TaskStates.Ready, true)]
    [InlineData(TaskStates.AutoReview, TaskStates.Progress, true)]
    [InlineData(TaskStates.AutoReview, TaskStates.Backlog, true)]
    [InlineData(TaskStates.AutoReview, TaskStates.HumanReview, false)]
    [InlineData(TaskStates.AutoReview, TaskStates.Escalated, false)]
    [InlineData(TaskStates.AutoReview, TaskStates.Archive, false)]
    [InlineData(TaskStates.Ready, TaskStates.Ready, false)]
    public void A_requeue_is_a_move_back_into_a_run_lane(string from, string to, bool expected)
        => Assert.Equal(expected, BatchIsolationGuardPolicy.IsRequeue(from, to));

    [Fact]
    public void A_red_parent_without_a_live_flight_does_not_refuse_its_member()
    {
        var manifest = SeedMember();
        var guard = new BatchIsolationGuard(_store);
        Assert.Null(guard.Find(Card()));

        BatchGateStates.Reach(_store, new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
            BatchPhase.Red, Result, 1, Now, "deterministic suite red"));
        Assert.Null(guard.Find(Card()));

        BatchGateStates.Reach(_store, new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
            BatchPhase.Paused, Result, 1, Now.AddMinutes(1), "unresolved-cohort:" + TaskKey));
        Assert.Null(guard.Find(Card()));
    }

    [Theory]
    [InlineData("proven-red", true)]
    [InlineData("superseded", true)]
    [InlineData("proven-green", false)]
    public void An_isolation_finding_releases_the_member_it_names(string status, bool released)
    {
        var manifest = SeedMember();
        BatchGateStates.Reach(_store, new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
            BatchPhase.Red, Result, 1, Now, "deterministic suite red"));
        _store.RecordIsolationFinding(new BatchGateIsolationFinding(Project, manifest.BatchId, TaskKey,
            "run-1", Result, Base, status, "evidence.json", Now));

        Assert.Equal(released, new BatchIsolationGuard(_store, activeFlight: _ => true).Find(Card()) is null);
    }

    [Fact]
    public void A_card_without_a_batch_owner_is_not_a_member()
    {
        SeedMember(batchOwned: false);
        Assert.Null(new BatchIsolationGuard(_store).Find(Card()));
    }

    private BatchGateManifest SeedMember(bool batchOwned = true)
    {
        var subject = new BatchGateSubject(TaskKey, Project, "repo", "develop", "full", "digest", "v1",
            "refs/heads/agent-studio/results/demo-7", Result, "run-1", 1, 1, Result,
            true, true, true, true, false, null, false, Now, 1);
        var members = new[] { subject };
        var manifest = new BatchGateManifest("batch-demo", Scope, Base,
            BatchGatePolicy.MembershipDigest(Base, Scope, members), Now, members, [], [TaskKey]);
        _store.CloseManifest(manifest);
        _store.Enqueue(new BatchGatePendingRecord(ReviewAttempt, subject, "repo", null, _folder,
            "merge", "default", Now));
        BatchGateOwnershipStore.Write(_folder, new BatchGateOwnership(ReviewAttempt, subject,
            BatchId: batchOwned ? manifest.BatchId : null));
        return manifest;
    }

    private TaskInfo Card() => new()
    {
        Id = TaskKey,
        State = TaskStates.AutoReview,
        ProjectName = Project,
        FolderPath = _folder,
    };
}
