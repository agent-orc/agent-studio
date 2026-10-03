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
/// </summary>
public sealed class BatchGateAssembler
{
    private readonly BatchGateStore _store;
    private readonly Func<BatchGateSubject, string, BatchReplayResult> _replay;
    private readonly Func<string, string, string?, BatchCandidateRefResult> _updateRef;

    public BatchGateAssembler(BatchGateStore store, GitService git, string repositoryPath)
        : this(store,
            (member, tip) => git.ReplayBatchMember(
                repositoryPath, member.ResultRef, member.ResultSha, tip),
            (candidateRef, next, old) => git.UpdateBatchCandidateRef(
                repositoryPath, candidateRef, next, old))
    {
    }

    public BatchGateAssembler(
        BatchGateStore store,
        Func<BatchGateSubject, string, BatchReplayResult> replay,
        Func<string, string, string?, BatchCandidateRefResult> updateRef)
    {
        _store = store;
        _replay = replay;
        _updateRef = updateRef;
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
                    replay.ConflictedFiles, "conflict", DateTimeOffset.UtcNow));
                ejected.Add(member.TaskKey);
                consecutiveConflicts++;
                _store.AppendState(new BatchGateState(
                    batchId, manifest.MembershipDigest, BatchPhase.Assembling,
                    tip, coordinatorFence, DateTimeOffset.UtcNow,
                    $"conflict:{member.TaskKey}"));
                if (!BatchGatePolicy.CascadeGuard(manifest.Members.Count,
                        ejected.Count, consecutiveConflicts)) continue;
                deferred.AddRange(manifest.Members.Skip(index + 1).Select(x => x.TaskKey));
                cascade = true;
                break;
            }
            if (!replay.Success || replay.TipSha is null
                || replay.Replacements.Count == 0
                || replay.Replacements.Any(x => string.IsNullOrWhiteSpace(x.OriginalSha)
                                                || string.IsNullOrWhiteSpace(x.RebasedSha)))
                throw new InvalidDataException(replay.Error ?? "Mechanical replay has no trustworthy SHA mapping.");
            var advanced = _updateRef(candidateRef, replay.TipSha, tip);
            if (!advanced.Success) throw new IOException(advanced.Error);
            _store.RecordReplay(new BatchGateReplayRecord(
                batchId, manifest.MembershipDigest, member.TaskKey,
                member.ResultSha, tip, replay.TipSha, replay.Replacements,
                [], "admitted", DateTimeOffset.UtcNow));
            tip = replay.TipSha;
            admitted.Add(member.TaskKey);
            consecutiveConflicts = 0;
            _store.AppendState(new BatchGateState(
                batchId, manifest.MembershipDigest, BatchPhase.Assembling,
                tip, coordinatorFence, DateTimeOffset.UtcNow));
        }
        _store.AppendState(new BatchGateState(
            batchId, manifest.MembershipDigest, BatchPhase.Assembled,
            tip, coordinatorFence, DateTimeOffset.UtcNow));
        return new BatchGateAssembly(batchId, manifest.MembershipDigest,
            candidateRef, tip, admitted, ejected, deferred, cascade);
    }
}
