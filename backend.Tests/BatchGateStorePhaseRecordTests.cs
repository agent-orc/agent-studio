using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>AGT-3563: the versioned phase and claim record behind every batch phase change.</summary>
public sealed class BatchGateStorePhaseRecordTests : IDisposable
{
    private const string Base = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Next = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly BatchGateScope Scope = new("project", "repo", "develop", "full", "digest", "v1");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "batch-phase-record-" + Guid.NewGuid().ToString("N"));
    private readonly BatchGateStore _store;

    public BatchGateStorePhaseRecordTests() => _store = new BatchGateStore(_root);

    public void Dispose()
    {
        BatchGateDurableWrites.ObserverForTesting = null;
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Formation_writes_one_record_that_claims_each_member_by_its_review_attempt()
    {
        var manifest = Close();

        var record = Assert.IsType<BatchPhaseRecord>(_store.ReadPhaseRecord(manifest.BatchId));
        Assert.Equal(1, record.Version);
        Assert.Equal(BatchPhase.Formed, record.State.Phase);
        Assert.Equal(manifest.Members.Select(member => "review-" + member.TaskKey),
            record.Members.Select(member => member.ReviewAttemptId));
        Assert.All(record.Members, member => Assert.Equal(BatchMemberDisposition.Claimed, member.Disposition));
        Assert.Equal([1L], History(manifest));
    }

    [Fact]
    public void A_terminal_change_records_each_members_disposition()
    {
        var manifest = Close();
        Walk(manifest, BatchPhase.Assembling, BatchPhase.Assembled, BatchPhase.Running,
            BatchPhase.Green, BatchPhase.Publishing);
        _store.RecordReplay(Replay(manifest, 0, "admitted"));
        _store.RecordReplay(Replay(manifest, 1, "conflict"));

        var published = _store.AppendState(State(manifest, BatchPhase.Published));

        Assert.Equal(7, published.Version);
        Assert.Equal([BatchMemberDisposition.Published, BatchMemberDisposition.Ejected, BatchMemberDisposition.Claimed],
            published.Members.Select(member => member.Disposition));
        Assert.Equal([1L, 2, 3, 4, 5, 6, 7], History(manifest));
    }

    [Fact]
    public void A_refused_change_writes_nothing()
    {
        var manifest = Close();

        var refused = Assert.Throws<BatchPhaseTransitionRefusedException>(() =>
            _store.AppendState(State(manifest, BatchPhase.Published)));
        _store.AppendState(State(manifest, BatchPhase.Assembling, fence: 5));
        var stale = _store.TryTransition(State(manifest, BatchPhase.Assembled, fence: 4));

        Assert.Equal("illegal-transition", refused.Refusal);
        Assert.False(stale.Accepted);
        Assert.Equal("stale-fence", stale.Refusal);
        Assert.Equal(BatchPhase.Assembling, _store.LatestState(manifest.BatchId).Phase);
        Assert.Equal([1L, 2], History(manifest));
    }

    [Fact]
    public void A_compare_and_swap_on_a_read_version_refuses_a_concurrent_change()
    {
        var manifest = Close();
        var read = _store.ReadPhaseRecord(manifest.BatchId)!.Version;
        _store.AppendState(State(manifest, BatchPhase.Formed) with { Reason = "repacked" });

        var raced = _store.TryTransition(State(manifest, BatchPhase.Abandoned), expectedVersion: read);

        Assert.Equal("stale-version", raced.Refusal);
        Assert.Equal("repacked", _store.LatestState(manifest.BatchId).Reason);
    }

    [Fact]
    public void A_crash_between_the_record_and_its_history_entry_is_completed_later()
    {
        var manifest = Close();
        var record = Path.Combine(_store.BatchDirectory(manifest.BatchId), "phase.json");
        BatchGateDurableWrites.ObserverForTesting = (path, stage) =>
        {
            if (stage == BatchGateWriteStage.Completed && path == record) throw new SimulatedCrash();
        };
        Assert.Throws<SimulatedCrash>(() => _store.AppendState(State(manifest, BatchPhase.Assembling)));
        BatchGateDurableWrites.ObserverForTesting = null;

        // The record is the commit point; its history entry is missing.
        Assert.Equal(BatchPhase.Assembling, _store.LatestState(manifest.BatchId).Phase);
        Assert.Equal([1L], History(manifest));
        var folder = _store.BatchDirectory(manifest.BatchId);
        Assert.Equal(BatchPhaseRepair.HistoryWritten, _store.ReconcilePhaseRecordIn(folder, Now));
        Assert.Equal(BatchPhaseRepair.None, _store.ReconcilePhaseRecordIn(folder, Now));
        Assert.Equal([1L, 2], History(manifest));

        BatchGateDurableWrites.ObserverForTesting = (path, stage) =>
        {
            if (stage == BatchGateWriteStage.Completed && path == record) throw new SimulatedCrash();
        };
        Assert.Throws<SimulatedCrash>(() => _store.AppendState(State(manifest, BatchPhase.Assembled)));
        BatchGateDurableWrites.ObserverForTesting = null;
        // The next change writes the missing entry before its own.
        _store.AppendState(State(manifest, BatchPhase.Running));
        Assert.Equal([1L, 2, 3, 4], History(manifest));
    }

    [Fact]
    public async Task Competing_writers_never_allocate_the_same_state_number()
    {
        var manifest = Close();
        const int writers = 4;
        const int changes = 25;
        using var start = new ManualResetEventSlim();
        // Each writer has its own store, as separate processes do; only the
        // cross-process file lock serializes them.
        var tasks = Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
        {
            var store = new BatchGateStore(_root);
            start.Wait();
            return Enumerable.Range(0, changes).Select(change => store.AppendState(
                State(manifest, BatchPhase.Formed) with { Reason = $"writer-{writer}-{change}" }).Version).ToArray();
        })).ToArray();
        start.Set();
        var versions = (await Task.WhenAll(tasks)).SelectMany(items => items).ToArray();

        Assert.Equal(writers * changes, versions.Distinct().Count());
        Assert.Equal(Enumerable.Range(2, writers * changes).Select(item => (long)item), versions.Order());
        Assert.Equal(Enumerable.Range(1, writers * changes + 1).Select(item => (long)item), History(manifest));
        Assert.Equal(writers * changes + 1, _store.ReadPhaseRecord(manifest.BatchId)!.Version);
    }

    [Fact]
    public void A_batch_closed_before_the_record_migrates_on_its_next_change()
    {
        var manifest = Close();
        var directory = _store.BatchDirectory(manifest.BatchId);
        File.Delete(Path.Combine(directory, "phase.json"));
        File.WriteAllText(Path.Combine(directory, "state", "00000002.json"), System.Text.Json.JsonSerializer.Serialize(
            State(manifest, BatchPhase.Assembling), new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web)));

        Assert.True(_store.HasState(manifest.BatchId));
        Assert.Equal(BatchPhase.Assembling, _store.LatestState(manifest.BatchId).Phase);
        var migrated = _store.AppendState(State(manifest, BatchPhase.Assembled));

        Assert.Equal(3, migrated.Version);
        Assert.Equal("review-task-0", migrated.Members[0].ReviewAttemptId);
        Assert.Equal([1L, 2, 3], History(manifest));
    }

    [Fact]
    public void A_manifest_without_state_gets_its_first_record_once()
    {
        var manifest = Formation();
        var file = Path.Combine(_store.BatchDirectory(manifest.BatchId), "manifest.json");
        BatchGateDurableWrites.ObserverForTesting = (path, stage) =>
        {
            if (stage == BatchGateWriteStage.Completed && path == file) throw new SimulatedCrash();
        };
        Assert.Throws<SimulatedCrash>(() => _store.CloseManifest(manifest));
        BatchGateDurableWrites.ObserverForTesting = null;

        Assert.False(_store.HasState(manifest.BatchId));
        Assert.Equal(BatchPhaseRepair.FirstRecordWritten,
            _store.ReconcilePhaseRecordIn(_store.BatchDirectory(manifest.BatchId), Now));
        Assert.False(_store.InitializeFormed(manifest, Now));
        Assert.Equal(BatchPhaseRepair.None,
            _store.ReconcilePhaseRecordIn(_store.BatchDirectory(manifest.BatchId), Now));
        var record = _store.ReadPhaseRecord(manifest.BatchId)!;
        Assert.Equal((1L, BatchPhase.Formed), (record.Version, record.State.Phase));
        Assert.Equal("review-task-2", record.Members[2].ReviewAttemptId);
    }

    [Fact]
    public void Formation_accepts_the_first_record_a_reconciliation_wrote_between_its_writes()
    {
        var manifest = Formation();
        var file = Path.Combine(_store.BatchDirectory(manifest.BatchId), "manifest.json");
        var reconciler = new BatchGateStore(_root);
        BatchGateDurableWrites.ObserverForTesting = (path, stage) =>
        {
            if (stage == BatchGateWriteStage.Completed && path == file)
                Assert.True(reconciler.InitializeFormed(manifest, Now));
        };

        _store.CloseManifest(manifest);
        BatchGateDurableWrites.ObserverForTesting = null;

        Assert.Equal(1, _store.ReadPhaseRecord(manifest.BatchId)!.Version);
        Assert.Equal([1L], History(manifest));
    }

    private BatchGateManifest Close()
    {
        var manifest = Formation();
        _store.CloseManifest(manifest);
        return manifest;
    }

    private BatchGateManifest Formation()
    {
        var members = Enumerable.Range(0, 3).Select(index => new BatchGateSubject(
            $"task-{index}", "project", "repo", "develop", "full", "digest", "v1",
            "refs/heads/agent-studio/results/task-" + index, Base, "run-" + index, 1, 1, Base,
            true, true, true, true, false, null, true, Now.AddMinutes(index), 0)).ToArray();
        var queued = members.Select(member => _store.Enqueue(new BatchGatePendingRecord(
            "review-" + member.TaskKey, member, "repo", null, "folder", "merge", "default", Now)).Subject).ToArray();
        return BatchGatePolicy.Form(queued, Scope, Base,
            new BatchGateFormationOptions(Enabled: true), Now.AddMinutes(20)).Manifest!;
    }

    private void Walk(BatchGateManifest manifest, params BatchPhase[] phases)
    {
        foreach (var phase in phases) _store.AppendState(State(manifest, phase));
    }

    private static BatchGateState State(BatchGateManifest manifest, BatchPhase phase, long fence = 1)
        => new(manifest.BatchId, manifest.MembershipDigest, phase,
            phase == BatchPhase.Formed ? null : Next, phase == BatchPhase.Formed ? 0 : fence, Now);

    private static BatchGateReplayRecord Replay(BatchGateManifest manifest, int index, string outcome)
        => new(manifest.BatchId, manifest.MembershipDigest, manifest.Members[index].TaskKey,
            manifest.Members[index].ResultSha, Base, outcome == "admitted" ? Next : null,
            outcome == "admitted" ? [new AgentStudio.Git.RebasedCommitReplacement(Base, Next)] : [],
            outcome == "conflict" ? ["README.md"] : [], outcome, Now);

    private long[] History(BatchGateManifest manifest)
        => Directory.EnumerateFiles(Path.Combine(_store.BatchDirectory(manifest.BatchId), "state"), "*.json")
            .Select(path => long.Parse(Path.GetFileNameWithoutExtension(path), System.Globalization.CultureInfo.InvariantCulture))
            .Order().ToArray();

    private sealed class SimulatedCrash : Exception;
}
