namespace AgentStudio.Pipeline;

/// <summary>Identity checks for a retained batch verdict used by a no-op integration.</summary>
public static class BatchGateCarryPolicy
{
    public static bool Accepts(
        BatchGateManifest manifest, BatchGateSubject member, BatchGateReplayRecord? replay,
        BatchGateRunRecord run, BatchGateRunVerdict? verdict)
        => replay is not null && verdict is not null
           && manifest.Members.Contains(member)
           && replay.BatchId == manifest.BatchId
           && replay.MembershipDigest == manifest.MembershipDigest
           && replay.TaskKey == member.TaskKey
           && replay.OriginalResultSha == member.ResultSha
           && replay.Outcome == "admitted"
           && replay.ConflictedFiles is { Count: 0 }
           && ReviewSubjectStore.IsValidResultSha(replay.TipAfterSha)
           && run.BatchId == manifest.BatchId
           && run.MembershipDigest == manifest.MembershipDigest
           && run.BaseSha == manifest.BaseSha
           && run.GateProfile == manifest.Scope.GateProfile
           && run.GateProfileDigest == manifest.Scope.GateProfileDigest
           && ReviewSubjectStore.IsValidResultSha(run.CandidateSha)
           && verdict.BatchId == run.BatchId
           && verdict.BatchRunId == run.BatchRunId
           && verdict.MembershipDigest == run.MembershipDigest
           && verdict.TestedCandidateSha == run.CandidateSha
           && verdict.GateProfileDigest == run.GateProfileDigest
           && verdict.EvidencePath == run.EvidencePath
           && verdict.Outcome == "pass";
}
