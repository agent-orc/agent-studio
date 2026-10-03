using AgentStudio.Git;
using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGateStoreTests : IDisposable
{
    private const string Base = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Next = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
    private static readonly BatchGateScope Scope = new("project", "repo", "develop", "full", "digest", "v1");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "batch-gate-test-" + Guid.NewGuid().ToString("N"));
    private readonly BatchGateStore _store;

    public BatchGateStoreTests() => _store = new BatchGateStore(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private static BatchGateSubject Subject(string key, int order) => new(
        key, "project", "repo", "develop", "full", "digest", "v1",
        "refs/heads/agent-studio/results/one", Base, "run-1", 1, 1, Base,
        true, true, true, true, false, null, true, Now.AddMinutes(order), order + 1);

    private BatchGateManifest Close(int count = 4)
    {
        var members = Enumerable.Range(0, count).Select(i => Subject($"task-{i}", i)).ToArray();
        var manifest = BatchGatePolicy.Form(members, Scope, Base,
            new BatchGateFormationOptions(Enabled: true), Now.AddMinutes(20)).Manifest!;
        _store.CloseManifest(manifest);
        return manifest;
    }

    [Fact]
    public void PendingReviewIsDurableIdempotentAndSequenced()
    {
        var first = _store.Enqueue(new BatchGatePendingRecord(
            "review-a", Subject("task-a", 0) with { EnqueueSequence = 0 },
            _root, _root, _root, "direct-merge", "task", Now));
        var replay = _store.Enqueue(new BatchGatePendingRecord(
            "review-a", Subject("task-a", 0) with { EnqueueSequence = 0 },
            _root, _root, _root, "direct-merge", "task", Now));
        var second = new BatchGateStore(_root).Enqueue(new BatchGatePendingRecord(
            "review-b", Subject("task-b", 1) with { EnqueueSequence = 0 },
            _root, _root, _root, "direct-merge", "task", Now));
        Assert.Equal(first.Subject.EnqueueSequence, replay.Subject.EnqueueSequence);
        Assert.True(second.Subject.EnqueueSequence > first.Subject.EnqueueSequence);
        Assert.Equal(2, new BatchGateStore(_root).ListPending().Count);
        _store.ResolvePending("review-a", "published");
        Assert.Single(_store.ListPending());
    }

    [Fact]
    public void ClosedManifestCannotAbsorbMembersOrChangeDigest()
    {
        var manifest = Close();
        Assert.Equal(4, _store.ReadManifest(manifest.BatchId).Members.Count);
        Assert.Throws<IOException>(() => _store.CloseManifest(manifest with
        {
            Members = manifest.Members.Take(3).ToArray(),
            MembershipDigest = BatchGatePolicy.MembershipDigest(Base, Scope, manifest.Members.Take(3).ToArray()),
        }));
        Assert.Throws<InvalidDataException>(() => _store.AppendState(new BatchGateState(
            manifest.BatchId, "wrong", BatchPhase.Green, Next, 1, Now)));
    }

    [Fact]
    public void ConflictDoesNotMoveTipAndCascadeDefersTail()
    {
        var manifest = Close(8);
        var current = Base;
        var updates = 0;
        var assembler = new BatchGateAssembler(_store,
            (member, tip) => member.TaskKey is "task-1" or "task-2" or "task-3"
                ? new BatchReplayResult(false, null, [], ["docs/conflict.md"], "conflict")
                : new BatchReplayResult(true, Next, [new RebasedCommitReplacement(Base, Next)], [], null),
            (_, next, old) =>
            {
                Assert.Equal(current, old ?? current);
                current = next;
                updates++;
                return new BatchCandidateRefResult(true, null);
            });
        var result = assembler.Assemble(manifest.BatchId, 1);
        Assert.Equal(["task-0"], result.AdmittedKeys);
        Assert.Equal(["task-1", "task-2", "task-3"], result.EjectedKeys);
        Assert.Equal(["task-4", "task-5", "task-6", "task-7"], result.DeferredKeys);
        Assert.Equal(Next, result.CandidateSha);
        Assert.Equal(2, updates); // Creation and one successful member only.
        Assert.True(result.CascadeStopped);
    }

    [Fact]
    public void MissingOrMismatchedEvidenceBlocksLaneRelease()
    {
        var manifest = Close();
        var member = manifest.Members[0];
        var evidence = Path.Combine(_root, "run-evidence.json");
        File.WriteAllText(evidence, "{\"tested\":true}");
        var run = new BatchGateRunRecord("run-1", manifest.BatchId,
            manifest.MembershipDigest, Base, Next, "full", "digest", "host", 1,
            ["dotnet test"], Now, evidence);
        _store.AppendState(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
            BatchPhase.Assembled, Next, 1, Now));
        _store.RecordRun(run);
        Assert.False(_store.CanRelease(member, manifest.BatchId, run.BatchRunId));
        Assert.Throws<InvalidDataException>(() => _store.RecordVerdict(
            new BatchGateRunVerdict(run.BatchRunId, manifest.BatchId,
                manifest.MembershipDigest, Next, "digest", "pass", "passed",
                "another-evidence-path", Now)));
        _store.RecordVerdict(new BatchGateRunVerdict(run.BatchRunId, manifest.BatchId,
            manifest.MembershipDigest, Next, "digest", "pass", "passed",
            evidence, Now));
        _store.RecordPublication(new BatchGatePublication(manifest.BatchId,
            manifest.MembershipDigest, run.BatchRunId, Base, Next, Next, 1, 1, Now));
        Assert.False(_store.CanRelease(member, manifest.BatchId, run.BatchRunId));
        _store.RecordReplay(new BatchGateReplayRecord(manifest.BatchId,
            manifest.MembershipDigest, member.TaskKey, Base, Base, Next,
            [new RebasedCommitReplacement(Base, Next)], [], "admitted", Now));
        Assert.Throws<InvalidDataException>(() => _store.RecordMember(new BatchGateMemberRecord(
            member.TaskKey, "wrong-attempt", 1, Base, [Next], manifest.BatchId,
            manifest.MembershipDigest, Base, Next, "digest", run.BatchRunId,
            "pass", evidence, Now)));
        _store.RecordMember(new BatchGateMemberRecord(
            member.TaskKey, member.RunAttempt, member.DeliveryEpoch, Base, [Next],
            manifest.BatchId, manifest.MembershipDigest, Base, Next, "digest",
            run.BatchRunId, "pass", evidence, Now));
        Assert.True(_store.CanRelease(member, manifest.BatchId, run.BatchRunId));
        Assert.False(_store.CanRelease(member with { ResultRef = "refs/heads/agent-studio/results/other" },
            manifest.BatchId, run.BatchRunId));
        File.Delete(evidence);
        Assert.False(_store.CanRelease(member, manifest.BatchId, run.BatchRunId));
        Assert.False(_store.CanRelease(member with { RunAttempt = "new" },
            manifest.BatchId, run.BatchRunId));
    }
}
