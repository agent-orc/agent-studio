using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

/// <summary>The active batch an Auto Review card is a member of, named on the refusal.</summary>
public sealed record ActiveBatchMembership(
    string Project, string TaskKey, string BatchId, BatchPhase Phase, IReadOnlyList<string> OtherMembers)
{
    public string PhaseName => Phase.ToString().ToLowerInvariant();
}

/// <summary>
/// AGT-3612, AGT-3719: an operator lane move must not take a member of an
/// active batch out of Auto Review. The batch would fail its membership check
/// and be abandoned, and its other members would wait for new batches. The
/// batch gate itself releases its members; an operator withdraws one
/// explicitly with a reason. Every lane-changing path reaches this rule through
/// <see cref="TaskTransitionService.MoveAsync"/>, so the single move, the
/// batch move and the other operator routes refuse the same move.
/// </summary>
public static class ActiveBatchMemberGuardPolicy
{
    public const string Code = "active-batch-member";

    /// <summary>Phases after which a batch no longer reads its membership.</summary>
    public static bool IsActive(BatchPhase phase)
        => phase is not (BatchPhase.Published or BatchPhase.Red or BatchPhase.Abandoned);

    public static bool LeavesAutoReview(string fromState, string toState)
        => fromState == TaskStates.AutoReview && !string.Equals(fromState, toState, StringComparison.Ordinal);

    /// <summary>
    /// True when the lane change comes from an operator: a person, an operator
    /// agent session or a service account. The runner, the batch gate and the
    /// orchestrator own their own Auto Review transitions.
    /// </summary>
    public static bool IsOperatorCause(string? cause)
        => TimelineActors.IsHuman(cause) || TimelineActors.IsOperatorAgent(cause)
           || TimelineActors.IsServiceAccount(cause);

    public static string Message(ActiveBatchMembership membership)
        => $"{membership.TaskKey} belongs to batch {membership.BatchId} ({membership.PhaseName}). "
           + $"Other members: {(membership.OtherMembers.Count == 0 ? "none" : string.Join(", ", membership.OtherMembers))}. "
           + "Wait for the batch to finish, or confirm withdrawal with withdrawFromBatch: true and a reason. "
           + "Withdrawal abandons this batch and returns the other members to planning.";

    /// <summary>The 409 body: the stable code, a sentence, and the batch it names.</summary>
    public static object Body(ActiveBatchMembership membership) => new
    {
        error = Code,
        message = Message(membership),
        batchId = membership.BatchId,
        phase = membership.PhaseName,
        otherMembers = membership.OtherMembers,
    };
}

/// <summary>
/// Collects the facts of <see cref="ActiveBatchMemberGuardPolicy"/> for one
/// card from the pending queue and the batch manifests.
/// </summary>
public sealed class ActiveBatchMemberGuard(BatchGateStore store)
{
    /// <summary>
    /// The active batch a move of <paramref name="card"/> to
    /// <paramref name="targetState"/> would break, or null when the move does
    /// not leave Auto Review or the card is in no active batch.
    /// </summary>
    public ActiveBatchMembership? Find(TaskInfo card, string targetState)
    {
        if (!ActiveBatchMemberGuardPolicy.LeavesAutoReview(card.State, targetState)) return null;
        var member = store.ListPending().FirstOrDefault(item =>
            string.Equals(item.JobFolderPath, card.FolderPath, StringComparison.Ordinal));
        if (member is null) return null;
        foreach (var manifest in store.ListManifests())
        {
            if (manifest.Scope.Project != member.Subject.Project
                || !manifest.Members.Any(item => item.TaskKey == member.Subject.TaskKey
                    && item.RunAttempt == member.Subject.RunAttempt)) continue;
            var phase = Phase(manifest.BatchId);
            if (!ActiveBatchMemberGuardPolicy.IsActive(phase)) continue;
            var others = manifest.Members.Where(item => item.TaskKey != member.Subject.TaskKey)
                .Select(item => item.TaskKey).ToArray();
            return new ActiveBatchMembership(member.Subject.Project, member.Subject.TaskKey,
                manifest.BatchId, phase, others);
        }
        return null;
    }

    /// <summary>
    /// An explicit withdrawal: requests the eject, then runs the lane move.
    /// A refused move, or one that throws (client abort, proxy timeout, IO
    /// failure), clears the eject again, so the batch keeps running and no
    /// per-task fallback starts for a card that never moved. The caller
    /// records the withdrawal only after a successful move.
    /// </summary>
    public async Task<MoveJobOutcome> WithdrawAsync(ActiveBatchMembership membership,
        Func<Task<MoveJobOutcome>> move)
    {
        store.RequestEject(membership.Project, membership.TaskKey);
        MoveJobOutcome? moved = null;
        try
        {
            moved = await move().ConfigureAwait(false);
            return moved;
        }
        finally
        {
            if (moved?.Status != MoveJobStatus.Success)
                store.ClearEject(membership.Project, membership.TaskKey);
        }
    }

    private BatchPhase Phase(string batchId)
    {
        try { return store.LatestState(batchId).Phase; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or InvalidDataException or System.Text.Json.JsonException)
        {
            // An unreadable state cannot prove the batch has finished, so the
            // member stays guarded.
            SilentCatch.Note(exception, $"batch state unreadable for {batchId}");
            return BatchPhase.Unknown;
        }
    }
}
