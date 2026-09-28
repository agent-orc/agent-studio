namespace AgentStudio.Pipeline;

public enum BatchPublishDecision
{
    FastForward,
    StaleBase,
    Superseded,
    LeaseLost,
    UntestedCandidate,
    MissingEvidence,
}

public static class BatchGatePublicationPolicy
{
    public static BatchPublishDecision Decide(
        BatchGateManifest manifest, BatchGateAssembly assembly,
        BatchGateRunRecord run, BatchGateRunVerdict verdict,
        IReadOnlyDictionary<string, BatchGateSubject> currentMembers,
        string currentIntegrationTip, bool coordinatorLeaseCurrent,
        bool refMutationLeaseCurrent, bool candidateDescendsFromPreTip)
    {
        if (!coordinatorLeaseCurrent || !refMutationLeaseCurrent)
            return BatchPublishDecision.LeaseLost;
        if (!string.Equals(currentIntegrationTip, manifest.BaseSha, StringComparison.OrdinalIgnoreCase))
            return BatchPublishDecision.StaleBase;
        if (assembly.AdmittedKeys.Count == 0
            || assembly.AdmittedKeys.Any(key => !currentMembers.TryGetValue(key, out var current)
                || !Current(manifest.Members.Single(member => member.TaskKey == key), current)))
            return BatchPublishDecision.Superseded;
        if (run.BatchId != manifest.BatchId
            || run.MembershipDigest != manifest.MembershipDigest
            || run.BaseSha != manifest.BaseSha
            || run.CandidateSha != assembly.CandidateSha
            || verdict.BatchRunId != run.BatchRunId
            || verdict.MembershipDigest != manifest.MembershipDigest
            || verdict.TestedCandidateSha != assembly.CandidateSha
            || verdict.GateProfileDigest != manifest.Scope.GateProfileDigest)
            return BatchPublishDecision.UntestedCandidate;
        if (verdict.Outcome != "pass" || string.IsNullOrWhiteSpace(verdict.EvidencePath))
            return BatchPublishDecision.MissingEvidence;
        if (candidateDescendsFromPreTip) return BatchPublishDecision.FastForward;
        return BatchPublishDecision.UntestedCandidate;
    }

    private static bool Current(BatchGateSubject frozen, BatchGateSubject current)
        => current.CurrentGeneration
           && frozen.TaskKey == current.TaskKey
           && frozen.RunAttempt == current.RunAttempt
           && frozen.DeliveryEpoch == current.DeliveryEpoch
           && frozen.FencingToken == current.FencingToken
           && frozen.GateProfileDigest == current.GateProfileDigest
           && frozen.PlatformVersion == current.PlatformVersion
           && frozen.IntegrationBranch == current.IntegrationBranch
           && frozen.ResultRef == current.ResultRef
           && frozen.ResultSha == current.ResultSha
           && current.ModelReviewPassed
           && current.BuildTestDeferredToBatch;
}
