using AgentStudio.TaskServer.Contracts.BatchGate;

namespace AgentStudio.Pipeline;

/// <summary>One successor may depend only on the predecessor's exact candidate.</summary>
public static class BatchStackingPolicy
{
    public static bool CanLead(BatchGateManifest predecessor,
        BatchGateState state, BatchGatePublication? publication,
        BatchGateManifest successor)
    {
        if (successor.StackedOnBatchId != predecessor.BatchId
            || predecessor.Scope != successor.Scope
            || state.CandidateSha != successor.BaseSha) return false;
        if (state.Phase == BatchPhase.Published)
            return publication is not null
                && publication.TestedCandidateSha == successor.BaseSha
                && publication.VerifiedRemoteSha == successor.BaseSha;
        return state.Phase is BatchPhase.Running or BatchPhase.Green or BatchPhase.Publishing;
    }

    public static bool CanPublish(BatchGateManifest predecessor,
        BatchGateState state, BatchGatePublication? publication,
        BatchGateManifest successor)
        => state.Phase == BatchPhase.Published
            && CanLead(predecessor, state, publication, successor);
}
