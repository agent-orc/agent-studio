namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire contracts for the P1 "task lifecycle extras" bundle: task creation
/// from the unscoped legacy body shape, the reverse task-reference lookup,
/// bulk reorder/batch-move, the archive listing, and the handful of
/// task-lifecycle side-actions (concept dossier, context-usage refresh,
/// integration rebase, planning closure, promote-concept, promote-to-coding)
/// that have no dedicated backing subsystem in the standalone Task Server.
/// </summary>
public sealed record StudioCreateTaskRequest(
    string ProjectId,
    string Title,
    string? Body = null,
    string? State = null,
    string? TaskId = null,
    string? TaskKey = null);

public sealed record TaskDependentDto(string TaskId, string TaskKey, string Title, string State);

public sealed record ReferenceStatusRequest(IReadOnlyList<string> Keys);

public sealed record TaskReferenceStatusDto(
    string Key,
    bool Exists,
    string? TaskId = null,
    string? TaskKey = null,
    string? Title = null,
    string? State = null);

public sealed record ReferenceStatusResponse(IReadOnlyList<TaskReferenceStatusDto> Items);

/// <summary>
/// An ordered list of task identities (id or task key). Tasks are grouped by
/// their current <c>(projectId, state)</c> lane and each group is assigned
/// dense, zero-based, ascending ranks in the order its members appear in
/// <see cref="TaskIds"/> - the same "contiguous rank per lane" convention
/// <c>TaskServerStudioTaskLifecycleStore.PlaceInLaneAsync</c> uses for
/// move/move-to-top.
/// </summary>
public sealed record ReorderTasksRequest(IReadOnlyList<string> TaskIds);

public sealed record ReorderTasksResponse(IReadOnlyList<TaskDto> Tasks);

public sealed record ArchivedTasksResponse(IReadOnlyList<TaskDto> Items, long Total);

public sealed record BatchMoveItemRequest(string TaskId, string TargetState, int? TargetIndex = null, string? Reason = null);

public sealed record BatchMoveRequest(IReadOnlyList<BatchMoveItemRequest> Items);

public sealed record BatchMoveItemResult(
    string TaskId,
    bool Success,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    TaskDto? Task = null);

/// <summary>
/// The batch-move "job" is resolved synchronously (every item is moved
/// through <see cref="StudioLifecycleCoordinator.MoveTaskAsync"/> before the
/// HTTP response is produced), so <see cref="Status"/> is always
/// <c>"completed"</c> the moment a batch is created - there is no background
/// worker. The durable row exists so the legacy poll-by-id shape
/// (<c>GET /batch-move/{batchId}</c>) keeps working unchanged.
/// </summary>
public sealed record BatchMoveJobResponse(
    string BatchId,
    string Status,
    IReadOnlyList<BatchMoveItemResult> Items,
    DateTime CreatedAt,
    DateTime? CompletedAt);

/// <summary>
/// The <c>action</c> discriminator stored in <c>task_lifecycle_actions</c>.
/// </summary>
public static class TaskLifecycleActionKinds
{
    public const string ConceptDossier = "concept-dossier";
    public const string ContextUsageRefresh = "context-usage-refresh";
    public const string IntegrationRebase = "integration-rebase";
    public const string PlanningClosure = "planning-closure";
    public const string PromoteConcept = "promote-concept";
    public const string PromoteToCoding = "promote-to-coding";
}

public sealed record ConceptDossierRequest(string? Path = null, bool NoDossierNeeded = false, string? Reason = null);

public sealed record PlanningClosureRequest(bool Declared, string? Reason = null);

public sealed record PromoteConceptRequest(IReadOnlyList<int>? ItemIndexes = null);

/// <summary>
/// Current durable status of one <c>(taskId, action)</c> lifecycle side
/// action. <see cref="Status"/> is <c>"not-requested"</c> when no row exists
/// yet, <c>"requested"</c> once recorded but not resolvable inside the Task
/// Server (the common case - see <see cref="TaskLifecycleActionKinds"/>
/// members other than <see cref="TaskLifecycleActionKinds.PlanningClosure"/>),
/// or <c>"completed"</c> for the one action
/// (<see cref="TaskLifecycleActionKinds.PlanningClosure"/>) that is a pure
/// state-tag flip the Task Server can resolve itself.
/// </summary>
public sealed record TaskLifecycleActionStatusDto(
    string TaskId,
    string Action,
    string Status,
    string? DetailJson,
    string? ActorId,
    DateTime RequestedAt,
    DateTime? CompletedAt);
