using AgentStudio.Git;

namespace AgentStudio.Pipeline;

public sealed record BatchGateAssembly(
    string BatchId, string MembershipDigest, string CandidateRef,
    string CandidateSha, IReadOnlyList<string> AdmittedKeys,
    IReadOnlyList<string> EjectedKeys, IReadOnlyList<string> DeferredKeys,
    bool CascadeStopped)
{
    /// <summary>
    /// AGT-3968: members that conflict only with a sibling admitted ahead of
    /// them. They stay queued for the next batch and open no agent round.
    /// </summary>
    public IReadOnlyList<string> SiblingDeferredKeys { get; init; } = [];
}

/// <summary>
/// Replays a closed manifest in order. A conflict cannot move the candidate
/// ref, and a failed ref or evidence write stops assembly before any gate run.
/// A member that cannot be mapped to one exact candidate commit is ejected
/// alone with its reason (AGT-3104); only an infrastructure failure stops the
/// whole assembly.
/// <para>AGT-3968: a member that conflicts with the candidate tip but merges
/// onto the batch base conflicts only with a sibling admitted ahead of it.
/// It is deferred, not ejected: an agent cannot resolve a conflict with a
/// delivery that is not on the integration branch yet (AGT-3897 and AGT-3891
/// on 17 November). It does not count toward the conflict cascade.</para>
/// </summary>
public sealed class BatchGateAssembler
{
    private readonly BatchGateStore _store;
    private readonly Func<BatchGateSubject, string, BatchReplayResult> _replay;
    private readonly Func<string, string, string?, BatchCandidateRefResult> _updateRef;
    private readonly Func<BatchGateSubject, string, IReadOnlyList<string>, IReadOnlyList<string>>? _cardsTouching;
    private readonly TimeProvider _time;

    public BatchGateAssembler(BatchGateStore store, GitService git, string repositoryPath,
        TimeProvider? time = null)
        : this(store,
            (member, tip) =>
            {
                using var capture = IntegrationConflictCapture.Begin(member.Project, member.TaskKey,
                    member.IntegrationBranch, member.ResultSha, reviewedAt: member.ReviewCompletedAtUtc);
                var result = git.ReplayBatchMember(repositoryPath, member.ResultRef, member.ResultSha, tip);
                IntegrationConflictCapture.Resolve(result.Success ? "mechanical" : "parked");
                return result;
            },
            (candidateRef, next, old) => git.UpdateBatchCandidateRef(
                repositoryPath, candidateRef, next, old),
            time,
            (member, tip, files) => git.CardsTouchingPaths(
                repositoryPath, member.ResultSha, tip, files, member.TaskKey))
    {
    }

    public BatchGateAssembler(
        BatchGateStore store,
        Func<BatchGateSubject, string, BatchReplayResult> replay,
        Func<string, string, string?, BatchCandidateRefResult> updateRef,
        TimeProvider? time = null,
        Func<BatchGateSubject, string, IReadOnlyList<string>, IReadOnlyList<string>>? cardsTouching = null)
    {
        _store = store;
        _replay = replay;
        _updateRef = updateRef;
        _cardsTouching = cardsTouching;
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
        var siblingDeferred = new List<string>();
        var tip = manifest.BaseSha;
        var conflicts = 0;
        var consecutiveConflicts = 0;
        var cascade = false;
        for (var index = 0; index < manifest.Members.Count; index++)
        {
            var member = manifest.Members[index];
            var replay = _replay(member, tip);
            if (replay.ConflictedFiles.Count > 0 && admitted.Count > 0
                && _replay(member, manifest.BaseSha) is { Success: true })
            {
                var named = _cardsTouching?.Invoke(member, tip, replay.ConflictedFiles) ?? [];
                var siblings = admitted.Where(key => named.Contains(key, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
                if (siblings.Length == 0) siblings = admitted.ToArray();
                _store.RecordReplay(new BatchGateReplayRecord(
                    batchId, manifest.MembershipDigest, member.TaskKey,
                    member.ResultSha, tip, null, [], replay.ConflictedFiles,
                    BatchReplayOutcomes.SiblingDeferred, _time.GetUtcNow(), Strategy(replay.Route),
                    $"Conflicts only with {string.Join(", ", siblings)} admitted ahead in this batch "
                    + $"({string.Join(", ", replay.ConflictedFiles)}); merges cleanly onto the integration "
                    + "branch, so it waits for the next batch instead of an agent round.")
                {
                    SiblingKeys = siblings,
                });
                IntegrationConflictLedger.Default.Resolve(member.Project, member.TaskKey,
                    member.ResultSha, member.IntegrationBranch, "deferred");
                siblingDeferred.Add(member.TaskKey);
                _store.AppendState(new BatchGateState(
                    batchId, manifest.MembershipDigest, BatchPhase.Assembling,
                    tip, coordinatorFence, _time.GetUtcNow(),
                    $"sibling-deferred:{member.TaskKey}"));
                continue;
            }
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
                    // to the per-task gate, does not count as a conflict and
                    // ends a run of consecutive conflicts.
                    _store.RecordReplay(new BatchGateReplayRecord(
                        batchId, manifest.MembershipDigest, member.TaskKey,
                        member.ResultSha, tip, null, [], [], "unattributable",
                        _time.GetUtcNow(), Strategy(replay.Route),
                        replay.Error ?? "Batch replay has no trustworthy SHA mapping."));
                    ejected.Add(member.TaskKey);
                    consecutiveConflicts = 0;
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
                [], "admitted", _time.GetUtcNow(), Strategy(replay.Route))
            {
                UnionResolvedHunks = replay.UnionResolvedHunks.Count > 0 ? replay.UnionResolvedHunks : null,
            });
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
            candidateRef, tip, admitted, ejected, deferred, cascade)
        {
            SiblingDeferredKeys = siblingDeferred,
        };
    }

    private static string Strategy(BatchReplayRoute route)
        => route == BatchReplayRoute.Merge ? BatchReplayStrategies.Merge : BatchReplayStrategies.Linear;
}
