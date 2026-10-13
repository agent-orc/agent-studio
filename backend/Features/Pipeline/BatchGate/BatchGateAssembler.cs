using AgentStudio.Git;

namespace AgentStudio.Pipeline;

public sealed record BatchGateAssembly(
    string BatchId, string MembershipDigest, string CandidateRef,
    string CandidateSha, IReadOnlyList<string> AdmittedKeys,
    IReadOnlyList<string> EjectedKeys, IReadOnlyList<string> DeferredKeys,
    bool CascadeStopped);

/// <summary>
/// Replays a closed manifest in order. A conflict cannot move the candidate
/// ref, and a failed ref or evidence write stops assembly before any gate run.
/// A member that cannot be mapped to one exact candidate commit is ejected
/// alone with its reason (AGT-3104); only an infrastructure failure stops the
/// whole assembly.
/// </summary>
public sealed class BatchGateAssembler
{
    private readonly BatchGateStore _store;
    private readonly Func<BatchGateSubject, string, BatchReplayResult> _replay;
    private readonly Func<string, string, string?, BatchCandidateRefResult> _updateRef;
    private readonly TimeProvider _time;

    public BatchGateAssembler(BatchGateStore store, GitService git, string repositoryPath,
        TimeProvider? time = null)
        : this(store,
            (member, tip) => git.ReplayBatchMember(
                repositoryPath, member.ResultRef, member.ResultSha, tip),
            (candidateRef, next, old) => git.UpdateBatchCandidateRef(
                repositoryPath, candidateRef, next, old),
            time)
    {
    }

    public BatchGateAssembler(
        BatchGateStore store,
        Func<BatchGateSubject, string, BatchReplayResult> replay,
        Func<string, string, string?, BatchCandidateRefResult> updateRef,
        TimeProvider? time = null)
    {
        _store = store;
        _replay = replay;
        _updateRef = updateRef;
        _time = time ?? TimeProvider.System;
    }

    public BatchGateAssembly Assemble(string batchId, long coordinatorFence)
    {
        var manifest = _store.ReadManifest(batchId);
        if (manifest.MembershipDigest != BatchGatePolicy.MembershipDigest(
                manifest.BaseSha, manifest.Scope, manifest.Members))
            throw new InvalidDataException("Closed manifest changed after formation.");
        var candidateRef = BatchGatePolicy.CandidateRef(manifest, coordinatorFence);
        var created = _updateRef(candidateRef, manifest.BaseSha, null);
        if (!created.Success) throw new IOException(created.Error);

        var admitted = new List<string>();
        var ejected = new List<string>();
        var deferred = new List<string>();
        var tip = manifest.BaseSha;
        var conflicts = 0;
        var consecutiveConflicts = 0;
        var cascade = false;
        for (var index = 0; index < manifest.Members.Count; index++)
        {
            var member = manifest.Members[index];
            var replay = _replay(member, tip);
            if (replay.ConflictedFiles.Count > 0)
            {
                _store.RecordReplay(new BatchGateReplayRecord(
                    batchId, manifest.MembershipDigest, member.TaskKey,
                    member.ResultSha, tip, null, replay.Replacements,
                    replay.ConflictedFiles, "conflict", _time.GetUtcNow(),
                    Strategy(replay.Route)));
                ejected.Add(member.TaskKey);
                conflicts++;
                consecutiveConflicts++;
                _store.AppendState(new BatchGateState(
                    batchId, manifest.MembershipDigest, BatchPhase.Assembling,
                    tip, coordinatorFence, _time.GetUtcNow(),
                    $"conflict:{member.TaskKey}"));
                if (!BatchGatePolicy.CascadeGuard(manifest.Members.Count,
                        conflicts, consecutiveConflicts)) continue;
                deferred.AddRange(manifest.Members.Skip(index + 1).Select(x => x.TaskKey));
                cascade = true;
                break;
            }
            switch (BatchGatePolicy.ReplayDisposition(replay))
            {
                case BatchReplayDisposition.Pause:
                    throw new InvalidDataException(replay.Error ?? "Batch member replay failed.");
                case BatchReplayDisposition.EjectUnattributable:
                    // The member says nothing about the others: it alone goes
                    // to the per-task gate and does not count as a conflict.
                    _store.RecordReplay(new BatchGateReplayRecord(
                        batchId, manifest.MembershipDigest, member.TaskKey,
                        member.ResultSha, tip, null, [], [], "unattributable",
                        _time.GetUtcNow(), Strategy(replay.Route),
                        replay.Error ?? "Batch replay has no trustworthy SHA mapping."));
                    ejected.Add(member.TaskKey);
                    _store.AppendState(new BatchGateState(
                        batchId, manifest.MembershipDigest, BatchPhase.Assembling,
                        tip, coordinatorFence, _time.GetUtcNow(),
                        $"unattributable:{member.TaskKey}"));
                    continue;
            }
            var advanced = _updateRef(candidateRef, replay.TipSha!, tip);
            if (!advanced.Success) throw new IOException(advanced.Error);
            _store.RecordReplay(new BatchGateReplayRecord(
                batchId, manifest.MembershipDigest, member.TaskKey,
                member.ResultSha, tip, replay.TipSha, replay.Replacements,
                [], "admitted", _time.GetUtcNow(), Strategy(replay.Route)));
            tip = replay.TipSha!;
            admitted.Add(member.TaskKey);
            consecutiveConflicts = 0;
            _store.AppendState(new BatchGateState(
                batchId, manifest.MembershipDigest, BatchPhase.Assembling,
                tip, coordinatorFence, _time.GetUtcNow()));
        }
        _store.AppendState(new BatchGateState(
            batchId, manifest.MembershipDigest, BatchPhase.Assembled,
            tip, coordinatorFence, _time.GetUtcNow()));
        return new BatchGateAssembly(batchId, manifest.MembershipDigest,
            candidateRef, tip, admitted, ejected, deferred, cascade);
    }

    private static string Strategy(BatchReplayRoute route)
        => route == BatchReplayRoute.Merge ? BatchReplayStrategies.Merge : BatchReplayStrategies.Linear;
}
