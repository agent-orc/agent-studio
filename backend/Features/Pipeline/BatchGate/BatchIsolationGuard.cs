using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

/// <summary>The isolating flight a card is a member of, named on the refusal.</summary>
public sealed record BatchIsolationMembership(string Project, string TaskKey, string FlightId);

/// <summary>
/// Pure membership rule for the operator-agent-level guardrail "No rounds to
/// members of a batch under isolation" (AGT-3483). A deterministic red batch
/// records <see cref="BatchPhase.Red"/> and then halves its members; the parent
/// leaves that phase when isolation pauses on an unresolved cohort, and each
/// member leaves isolation when a finding proves it red or superseded, its
/// pending record resolves, or a per-task fallback takes it over (the ownership
/// then names no batch). Until then a continue, steer or requeue would change
/// the delivery the probes are testing. Eject stays the member action.
/// </summary>
public static class BatchIsolationGuardPolicy
{
    public const string Code = "batch-isolation-active";

    public static bool IsIsolating(
        string? owningBatchId,
        bool pendingActive,
        BatchPhase? latestPhase,
        bool memberLeftIsolation)
        => !string.IsNullOrWhiteSpace(owningBatchId)
           && pendingActive
           && latestPhase == BatchPhase.Red
           && !memberLeftIsolation;

    /// <summary>
    /// Lanes a move puts a card back into for another run: a requeue. Moves
    /// to the decision lanes, Completed or the archive are not rounds.
    /// </summary>
    private static readonly HashSet<string> RequeueLanes = new(StringComparer.Ordinal)
    {
        TaskStates.Backlog, TaskStates.Preparation, TaskStates.OrchestratorPrep,
        TaskStates.Ready, TaskStates.Progress,
    };

    public static bool IsRequeue(string fromState, string toState)
        => !string.Equals(fromState, toState, StringComparison.Ordinal) && RequeueLanes.Contains(toState);

    /// <summary>The 409 body: the stable code, a sentence, and the flight it names.</summary>
    public static object Body(BatchIsolationMembership membership) => new
    {
        error = Code,
        message = Message(membership),
        flight = membership.FlightId,
        project = membership.Project,
        taskKey = membership.TaskKey,
    };

    public static string Message(BatchIsolationMembership membership)
        => $"{membership.TaskKey} is a member of batch flight {membership.FlightId}, which is isolating a red result. "
           + "Continue, steer and requeue wait until isolation ends; eject the member to act on it now.";
}

/// <summary>
/// Collects the facts of <see cref="BatchIsolationGuardPolicy"/> for one card
/// from its batch ownership marker and the batch gate store.
/// </summary>
public sealed class BatchIsolationGuard(BatchGateStore store)
{
    /// <summary>Statuses with which an isolation finding takes a member out of the running isolation.</summary>
    private static readonly HashSet<string> LeavingStatuses = new(StringComparer.Ordinal) { "proven-red", "superseded" };

    public BatchIsolationMembership? Find(TaskInfo card)
    {
        BatchGateOwnership? ownership;
        try { ownership = BatchGateOwnershipStore.Read(card.FolderPath); }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            // An unreadable marker already fails the lane release closed; it
            // does not make the card a member of a flight it cannot name.
            SilentCatch.Note(exception, $"batch ownership unreadable in {card.FolderPath}");
            return null;
        }
        if (ownership?.BatchId is not { } batchId) return null;

        var subject = ownership.Subject;
        var pendingActive = store.ListPending().Any(item => item.ReviewAttemptId == ownership.ReviewAttemptId);
        BatchPhase? phase;
        try { phase = store.LatestState(batchId).Phase; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or InvalidDataException or System.Text.Json.JsonException)
        {
            SilentCatch.Note(exception, $"batch state unreadable for {batchId}");
            phase = null;
        }
        var left = store.ListIsolationFindings(subject.Project).Any(finding =>
            finding.ParentBatchId == batchId
            && finding.TaskKey == subject.TaskKey
            && finding.RunAttempt == subject.RunAttempt
            && LeavingStatuses.Contains(finding.Status));

        return BatchIsolationGuardPolicy.IsIsolating(batchId, pendingActive, phase, left)
            ? new BatchIsolationMembership(subject.Project, subject.TaskKey, batchId)
            : null;
    }
}
