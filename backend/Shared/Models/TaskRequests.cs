using AgentStudio.Registry;

namespace AgentStudio.Shared;

/// <summary>
/// Body for <c>POST /api/tasks/{id}/review-evidence/{evidenceId}/follow-up</c>.
/// Optional title override; the endpoint defaults to the finding's title when
/// omitted. The created task is queued in the same project as the source job
/// and lands in <c>1-preparation</c>; the user promotes it to <c>2-ready</c>
/// when they want auto-pickup to run it.
/// </summary>
public record CreateFollowupFromEvidenceRequest
{
    public string? Title { get; init; }
    public string? TargetState { get; init; }
}

/// <summary>
/// Response shape for the follow-up endpoint. <c>JobId</c> remains the
/// storage slug for backwards compatibility; <c>TaskKey</c> is the stable,
/// globally resolvable reference clients should put in links.
/// </summary>
public record CreateFollowupFromEvidenceResponse
{
    public string JobId { get; init; } = "";
    public string? TaskKey { get; init; }
    public string TargetState { get; init; } = TaskStates.Preparation;
}

/// <summary>
/// Body for <c>POST /api/tasks/{id}/external-completion</c>. Reconciles a task
/// that was completed outside the runner (operator chat, external agent, a
/// remote host) in one atomic call, per
/// <c>docs/concepts/out-of-band-task-completion.md</c> §3: writes
/// <c>status.md</c> + <c>results/deliverables.md</c>, terminalizes
/// <c>lifecycle.json</c>, appends an <c>external</c> timeline entry, moves the
/// lane, and commits the workspace evidence.
/// </summary>
public record ExternalCompletionRequest
{
    /// <summary>Result summary that replaces the stale <c>status.md</c> text. Required.</summary>
    public string? Summary { get; init; }
    /// <summary>What was delivered and where (repo paths + commits, or URLs).</summary>
    public List<ExternalDeliverable>? Deliverables { get; init; }
    /// <summary>Who or which channel did the work (operator name, agent id, "chat", ...).</summary>
    public string? Source { get; init; }
    /// <summary>
    /// Optional destination lane. Defaults to <c>5-human-review</c> (the card
    /// still gets a quick operator confirmation). Must be a valid
    /// <see cref="TaskStates"/> value.
    /// </summary>
    public string? TargetState { get; init; }
    /// <summary>
    /// Optional open checklist items that require operator action. Remote
    /// runners use this when a worktree could not be secured and therefore
    /// remains on its host.
    /// </summary>
    public List<string>? GateItems { get; init; }

    /// <summary>
    /// AGT-2220: the full 40-character commit SHA this completion claims was
    /// delivered into the target repository. The backend re-verifies it against
    /// git; a coding-mode card without a provable SHA is never stamped
    /// completed. Prose in <see cref="Summary"/> is not proof.
    /// </summary>
    public string? ResultSha { get; init; }

    /// <summary>
    /// AGT-2220: the ref in the target repository that is expected to carry
    /// <see cref="ResultSha"/> (branch name or full ref). Optional - without it
    /// the SHA is searched across all remote refs.
    /// </summary>
    public string? ResultRef { get; init; }

    /// <summary>
    /// Pickup base for this delivery attempt. A result equal to this SHA is an
    /// empty delivery and is rejected before any completion evidence is written.
    /// </summary>
    public string? BaseSha { get; init; }
}

/// <summary>One delivered artifact recorded in <c>results/deliverables.md</c>.</summary>
public record ExternalDeliverable
{
    /// <summary>Repo-relative path (with an optional <c>@sha</c> commit hint) of the delivered artifact.</summary>
    public string? Path { get; init; }
    /// <summary>External URL when the deliverable lives outside the repo.</summary>
    public string? Url { get; init; }
    /// <summary>Free-form note about this deliverable.</summary>
    public string? Note { get; init; }
}

/// <summary>Typed outcome of the external-completion service, mapped to HTTP by the endpoint.</summary>
public enum ExternalCompletionStatus
{
    Success,
    NotFound,
    InvalidRequest,
    MoveConflict,
    MoveFailed,

    /// <summary>The claimed result is identical to the attempt base.</summary>
    NoDelivery,

    /// <summary>
    /// AGT-2220: the claimed delivery could not be proven against the target
    /// repository. No completion stamp was written; the card was routed to an
    /// honest <c>unverified-delivery</c> state instead.
    /// </summary>
    UnverifiedDelivery
}

/// <summary>
/// Result of an external-completion attempt. <see cref="TargetState"/> and
/// <see cref="EvidenceCommitSha"/> are populated only on
/// <see cref="ExternalCompletionStatus.Success"/>.
/// </summary>
public record ExternalCompletionOutcome(
    ExternalCompletionStatus Status,
    string? Message = null,
    string? JobId = null,
    string? TargetState = null,
    string? EvidenceCommitSha = null);

/// <summary>200 body for <c>POST /api/tasks/{id}/external-completion</c>.</summary>
public record ExternalCompletionResponse
{
    public string JobId { get; init; } = "";
    public string TargetState { get; init; } = TaskStates.HumanReview;
    public string Source { get; init; } = "";
    /// <summary>Short SHA of the workspace evidence commit, or null when nothing was committed.</summary>
    public string? EvidenceCommitSha { get; init; }
}

public record MoveJobRequest
{
    public string TargetState { get; init; } = "";
    /// <summary>
    /// Explicit one-shot operator decision to complete an accepted coding card
    /// without integration. Valid only when <see cref="TargetState"/> is
    /// <see cref="TaskStates.Completed"/> and never inferred by the server.
    /// </summary>
    public bool OperatorOverride { get; init; }

    /// <summary>
    /// Optional operator rationale for the lane move. It is persisted on the
    /// timeline and, for a requeue out of a decision lane, on the fresh
    /// review-attempt epoch boundary.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Optional 0-based insertion slot in the target lane. When supplied,
    /// the move pins the dropped job to that position and rewrites every
    /// other job's <c>order</c> in the same lane + project so the
    /// resulting sequence is stable. <c>null</c> preserves the legacy
    /// behaviour: the folder moves and the job keeps whatever <c>order</c>
    /// value it had in the source lane.
    /// </summary>
    public int? TargetIndex { get; init; }
}

public enum MoveJobStatus
{
    Success,
    NotFound,
    TargetFolderExists,
    DirectoryLocked,
    Failure,
    SourceStateMismatch,
    IntegrationFailed
}

/// <summary>
/// Result of a <see cref="AgentStudio.Tasks.TaskStateMachine.MoveJob"/>
/// call. <paramref name="NewFolderPath"/> is populated only on
/// <see cref="MoveJobStatus.Success"/> and carries the absolute path of the
/// post-move job folder. Callers that want to write into the moved folder
/// (chat-log line, follow-up file) MUST use this rather than re-finding the
/// job through the scanner — the cache may not yet reflect the move, and
/// a stale path would silently recreate the source folder on first write.
/// </summary>
public record MoveJobOutcome(MoveJobStatus Status, string? Message = null, string? NewFolderPath = null);

/// <summary>Result of <c>POST /api/tasks/{id}/restore-from-failed-pickup</c>.</summary>
public enum RestoreFromFailedPickupStatus
{
    /// <summary>Folder was restored into the target lane under the resolved slug.</summary>
    Success,
    /// <summary>Slug is not in <c>3a-failed-pickup</c>: either it does not exist
    /// or it has already been restored. Distinguished from <see cref="NotFound"/>
    /// by the caller (the endpoint maps <c>NotFound</c> to 404 and <c>NoOp</c>
    /// to 200 with a status payload so the call is idempotent).</summary>
    NoOp,
    /// <summary>No folder with this slug exists in <c>3a-failed-pickup</c>.</summary>
    NotFound,
    /// <summary>A folder with the resolved slug already exists in the target lane.</summary>
    TargetFolderExists,
    /// <summary>The slug did not match the dead-letter shape <c>&lt;original&gt;-pickup-failed-&lt;yyyy-mm-dd&gt;</c>.</summary>
    InvalidSlug,
    /// <summary>Filesystem operation failed unexpectedly.</summary>
    Failure
}

/// <summary>Outcome of a <c>POST /api/tasks/{id}/restore-from-failed-pickup</c> call.
/// On <see cref="RestoreFromFailedPickupStatus.Success"/> the caller can read
/// <see cref="RestoredSlug"/> (the slug the folder now lives under) and
/// <see cref="OriginalSlug"/> (the slug parsed back from the dead-letter name).</summary>
public record RestoreFromFailedPickupOutcome(
    RestoreFromFailedPickupStatus Status,
    string? RestoredSlug = null,
    string? OriginalSlug = null,
    string? SourceSlug = null,
    string? Message = null);

/// <summary>Body for <c>POST /api/tasks/{id}/restore-from-failed-pickup</c>.
/// Body is optional; defaults to restoring the original slug.</summary>
public record RestoreFromFailedPickupRequest
{
    /// <summary>When <c>true</c>, keep the <c>-pickup-failed-&lt;utc&gt;</c>
    /// suffix on the restored folder. Default <c>false</c>: strip the suffix
    /// so the slug matches the pre-dead-letter name.</summary>
    public bool KeepDeadLetterSlug { get; init; }
}

/// <summary>
/// Per-item entry for <c>POST /api/tasks/batch-move</c>. Each item names
/// the job, the watch path that disambiguates a slug that lives in two
/// workspaces, the target lane, and an optional 0-based insertion slot
/// (<see cref="MoveJobRequest.TargetIndex"/>). Items are processed
/// independently by a background job: a failure on one item does not roll
/// back items that already moved. The endpoint returns a job handle and the
/// caller reads progress from <c>GET /api/tasks/batch-move/{batchId}</c>.
/// </summary>
public record BatchMoveItem
{
    public string JobId { get; init; } = "";
    public string? WatchPath { get; init; }
    public string TargetState { get; init; } = "";
    public int? TargetIndex { get; init; }
    public string? Reason { get; init; }
}

public record BatchMoveRequest
{
    public List<BatchMoveItem> Items { get; init; } = [];
}

/// <summary>
/// Per-item outcome string for the batch-move response:
/// <list type="bullet">
/// <item><description><c>moved</c>: folder transitioned to the target lane.</description></item>
/// <item><description><c>not-found</c>: no job folder matched the (jobId, watchPath) pair.</description></item>
/// <item><description><c>conflict</c>: a non-recoverable target path conflict blocked the move.</description></item>
/// <item><description><c>rejected</c>: invalid input (unknown lane name, empty jobId, etc.).</description></item>
/// <item><description><c>failed</c>: an unexpected IO error blocked the move.</description></item>
/// </list>
/// </summary>
public record BatchMoveItemResult
{
    public string JobId { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Message { get; init; }
}

public record CreateTaskRequest
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public int Order { get; init; } = 999;
    public string Agent { get; init; } = "claude";

    /// <summary>
    /// Preferred, path-free project handle: a short code / Kürzel (e.g.
    /// <c>ASS</c>) or a stable project id (<c>PROJ-NNN</c>). The server resolves
    /// it to the project's storage location, so the filesystem layout never
    /// travels over the wire. When set, this takes precedence over
    /// <see cref="WatchPath"/>.
    /// </summary>
    public string? Project { get; init; }

    /// <summary>
    /// Deprecated absolute filesystem path of the target project. Retained for
    /// legacy callers during the watchPath-encapsulation migration; new callers
    /// should send <see cref="Project"/> (Kürzel or <c>PROJ-NNN</c>) instead.
    /// Also accepts a <c>PROJ-NNN</c> id or short code, which is resolved
    /// server-side.
    /// </summary>
    public string WatchPath { get; init; } = "";
    public string? PromptMarkdown { get; init; }
    /// <summary>
    /// Optional structured delivery boundary used by requirement-fit review.
    /// Use <c>bounded-slice</c> for one independently shippable Dossier or Epic
    /// slice; omit it when the whole task body must be delivered together.
    /// </summary>
    public TaskAcceptanceScope? AcceptanceScope { get; init; }
    public string? Model { get; init; }
    public string? ThinkingLevel { get; init; }
    /// <summary>
    /// Provenance for model qualification. Null preserves the legacy API rule
    /// that a supplied value is explicit; false means the UI merely
    /// materialized its default and qualification may replace it.
    /// </summary>
    public bool? ModelExplicit { get; init; }
    public bool? ThinkingLevelExplicit { get; init; }
    public string? TargetState { get; init; }
    /// <summary>Optional CLI backend (claude|codex|gemini). Defaults to claude when omitted.</summary>
    public string? CliType { get; init; }
    /// <summary>Card kind: <c>task</c> (default) or <c>epic</c>. See <see cref="TaskKinds"/>.</summary>
    public string? Kind { get; init; }
    /// <summary>Optional parent epic id (assignment way 1: at create time). The new card is created as a sub-task of this epic.</summary>
    public string? EpicId { get; init; }
    /// <summary>Execution mode: <c>coding</c> (default) | <c>planning</c> | <c>research</c> | <c>concept</c>. See <see cref="TaskModes"/>.</summary>
    public string? Mode { get; init; }
    /// <summary>Allow web search/fetch for this run. When null, defaults by mode (research = on, else off).</summary>
    public bool? AllowWebAccess { get; init; }
    /// <summary>Explicitly declare that the task is expected to have no delivery branch.</summary>
    public bool NoBranchExpected { get; init; }
    /// <summary>
    /// Optional client identity that owns the new job. When omitted, the
    /// endpoint falls back to the X-Client-Id header on the incoming
    /// request, then to <see cref="DefaultClientIdentity.Id"/>.
    /// </summary>
    public string? OwnerClientId { get; init; }

    /// <summary>
    /// When <c>true</c>, the new job is marked as an E2E test fixture and is
    /// hidden from the default kanban response. Used by Playwright specs that
    /// create real job folders to keep their fixtures out of the user's view
    /// on stable.
    /// </summary>
    public bool Fixture { get; init; }

    /// <summary>
    /// Structural classification (<c>bug</c>, <c>feature</c>, <c>chore</c>).
    /// Defaults to <see cref="TaskTypes.Chore"/> when omitted. Validated on
    /// the server and normalized via <see cref="TaskTypes.Normalize"/>; legacy
    /// <c>"user-story"</c> input maps to <see cref="TaskTypes.Feature"/>.
    /// </summary>
    public string? TaskType { get; init; }

    /// <summary>
    /// Optional tag ids to attach to the new job. Unknown ids are dropped
    /// silently; the registry is the source of truth for label and colour.
    /// </summary>
    public List<string>? Tags { get; init; }

    /// <summary>
    /// Affected surface/component supplied by task-creation clients. When
    /// present the server resolves the destination from project ownership
    /// metadata instead of assuming the navigation project owns the fix.
    /// </summary>
    public ComponentRoutingRequest? Routing { get; init; }
    /// <summary>Optional caller-derived prefix. Must match the resolved destination.</summary>
    public string? RequestedTaskPrefix { get; init; }
}

/// <summary>
/// Payload from <c>GET /api/tasks/{id}/promote-to-coding</c>: a fully
/// pre-filled coding-task draft derived from a finished planning task.
/// The frontend seeds the existing create-task modal with these fields so
/// the modal stays the single source of truth for the create UX. Images
/// are returned as fetchable references (not inline bytes); the modal
/// re-uploads them byte-for-byte into the new task's <c>attachments/</c>
/// on save. See docs/concepts/planning-research-task-kinds-2026-05.md.
/// </summary>
public record PromoteToCodingResponse
{
    /// <summary>Title for the new coding task (the planning task's title, or its report heading).</summary>
    public string Title { get; init; } = "";

    /// <summary>Prompt body, extracted from the report's <c>## Proposed task prompt</c> section.</summary>
    public string PromptMarkdown { get; init; } = "";

    /// <summary>Always <see cref="TaskModes.Coding"/> — the promotion target mode.</summary>
    public string Mode { get; init; } = TaskModes.Coding;

    /// <summary>Always <see cref="TaskStates.Preparation"/> so the user gets one review pass before pickup (decision 3).</summary>
    public string TargetState { get; init; } = TaskStates.Preparation;

    /// <summary>Watch path of the source planning task; the new task lands in the same project.</summary>
    public string WatchPath { get; init; } = "";

    /// <summary>Project name of the source planning task (display convenience).</summary>
    public string ProjectName { get; init; } = "";

    /// <summary>Every image under the planning task's <c>results/</c> and <c>attachments/</c> folders, deduped by file name.</summary>
    public List<PromoteAttachmentRef> Attachments { get; init; } = [];
}

/// <summary>
/// One copyable image attachment surfaced by
/// <see cref="PromoteToCodingResponse"/>. The frontend fetches
/// <see cref="Url"/> as a blob, then re-uploads it into the new task.
/// </summary>
public record PromoteAttachmentRef
{
    public string FileName { get; init; } = "";

    /// <summary>Source folder: <c>results</c> or <c>attachments</c>.</summary>
    public string Source { get; init; } = "";

    /// <summary>Relative API URL that serves the image bytes from the source task.</summary>
    public string Url { get; init; } = "";
}

/// <summary>
/// One entry in the workspace-level tag registry. Stored as one element of
/// the JSON array at <c>&lt;TaskRepository&gt;/tags.json</c> and surfaced via
/// <c>GET /api/tags</c>. The id is the lookup key referenced from each
/// <see cref="TaskInfo.Tags"/> entry; label, colour, and description are
/// pure display metadata.
/// </summary>
public record TagRegistryEntry
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Color { get; init; } = "#94a3b8";
    public string Description { get; init; } = "";
}

/// <summary>
/// Body for <c>POST /api/tags</c>. When <see cref="Id"/> is omitted, the
/// server derives it from <see cref="Label"/> by lowercasing and stripping
/// to <c>[a-z0-9-]</c>.
/// </summary>
public record CreateTagRequest
{
    public string? Id { get; init; }
    public string Label { get; init; } = "";
    public string? Color { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// Body for <c>PUT /api/tasks/{id}/tags</c>. Replace-all: the supplied list is
/// the new full set of tag ids on the job. Empty list clears tags. Unknown
/// ids are accepted (the registry may evolve), but they will render as a
/// ghost chip until the registry catches up or the job is re-tagged.
/// </summary>
public record SetJobTagsRequest
{
    public List<string> Tags { get; init; } = [];
}

/// <summary>
/// Body for <c>PUT /api/tasks/{id}/task-type</c>. Validated via
/// <see cref="TaskTypes.Normalize"/>; an unknown value collapses to
/// <see cref="TaskTypes.Chore"/>.
/// </summary>
public record SetJobTaskTypeRequest
{
    public string TaskType { get; init; } = TaskTypes.Chore;
}

/// <summary>
/// Body for <c>PUT /api/tasks/{id}/release</c>. The flag is an explicit content
/// approval and is never inferred from the task's lane.
/// </summary>
public record SetJobReleasedRequest
{
    public bool Released { get; init; }
}

public record ReorderRequest
{
    public List<string> JobIds { get; init; } = [];
    public List<TaskOrderItem> Jobs { get; init; } = [];
}

public record OrphanFolderDeleteRequest
{
    public string? WatchPath { get; init; }
    public string? Lane { get; init; }
    public string? Folder { get; init; }
}

public record TaskOrderItem
{
    public string JobId { get; init; } = "";
    public string WatchPath { get; init; } = "";
}

public record ChangeProjectRequest
{
    /// <summary>
    /// Preferred, path-free handle of the destination project: a short code /
    /// Kürzel (e.g. <c>ASS</c>) or a stable <c>PROJ-NNN</c> id. Resolved
    /// server-side to the project's storage location; wins over the deprecated
    /// <see cref="TargetWatchPath"/> when set.
    /// </summary>
    public string? TargetProject { get; init; }

    /// <summary>
    /// Deprecated absolute filesystem path of the destination project. Retained
    /// for legacy callers during the watchPath-encapsulation migration; new
    /// callers should send <see cref="TargetProject"/> instead.
    /// </summary>
    public string TargetWatchPath { get; init; } = "";
}

public record UpdateJobFileRequest
{
    public string FileName { get; init; } = "";
    public string Content { get; init; } = "";
}

public record GitCommitRequest
{
    public string Message { get; init; } = "";
}

public record SetAutoCommitRequest
{
    public bool Enabled { get; init; }
}

/// <summary>
/// Body for <c>PUT /api/projects/{name}/build-profile</c> (Slice P / ASS-1663).
/// Declares or edits the project's build profile. A first declaration starts in
/// <see cref="BuildProfileStatuses.Declared"/>. An edit to a validated profile
/// keeps bounded pickup grace while exact-profile revalidation is pending.
/// </summary>
public record SetBuildProfileRequest
{
    public string? Stack { get; init; }
    public string? InstallCmd { get; init; }
    public IReadOnlyList<string>? BuildCmds { get; init; }
    public IReadOnlyList<string>? TestCmds { get; init; }
    public IReadOnlyList<string>? Lockfiles { get; init; }
    public IReadOnlyList<string>? PreserveGlobs { get; init; }
    public int? PoolSize { get; init; }
}

/// <summary>
/// Body for <c>PUT /api/projects/{name}/max-parallelism</c> (ADR-0052). The
/// value is clamped to <c>&gt;= 1</c> server-side; <c>1</c> means sequential.
///
/// <para>
/// DEPRECATED for remote execution (AGT-2302 / AGT-2376): a remotely executed
/// project takes its concurrency from the host ceiling. The setting still
/// governs the local <c>ProjectRunner</c> (<c>ParallelSlotPolicy</c>), which is
/// why the route exists at all. Remove after 2026-10-01, when the CAR rework of
/// local execution replaces it.
/// </para>
/// </summary>
public record SetMaxParallelismRequest
{
    public int MaxParallelism { get; init; } = 1;
}

/// <summary>Body for the server-owned remote runner assignment.</summary>
public record SetExecutionRunnerRequest
{
    /// <summary>
    /// Legacy composite field. Values <c>auto-continuous</c>, <c>manual</c>,
    /// and <c>paused</c> change pickup intent; any registered runner id means
    /// automatic pickup at that runner; null/local selects local execution.
    /// </summary>
    public string? ExecutionRunner { get; init; }

    /// <summary>Canonical pickup dimension: auto, manual, or paused.</summary>
    public string? PickupMode { get; init; }

    /// <summary>Canonical placement dimension: local or a registered runner id.</summary>
    public string? ExecutionLocation { get; init; }

    /// <summary>Legacy compatibility input. False resolves placement to local.</summary>
    public bool? RemoteExecutionEnabled { get; init; }
}

/// <summary>
/// Body for <c>PUT /api/projects/{name}/integration-branch</c> (ADR-0052).
/// Blank reverts to the default integration branch.
/// </summary>
public record SetIntegrationBranchRequest
{
    public string? Branch { get; init; }
}

/// <summary>
/// Body for <c>PUT /api/projects/{name}/integration-strategy</c> (ADR-0052).
/// Unknown values normalize to <see cref="IntegrationStrategies.DirectMerge"/>.
/// </summary>
public record SetIntegrationStrategyRequest
{
    public string Strategy { get; init; } = IntegrationStrategies.DirectMerge;
}

public record SetAutoPushStrategyRequest
{
    public string Strategy { get; init; } = AutoPushStrategies.AlwaysImmediate;
}

/// <summary>
/// Body for <c>PUT /api/projects/{name}/cli-mode</c>. Sets the per-project
/// permission mode for one CLI. A null / empty <see cref="Mode"/> clears the
/// override so the CLI reverts to the platform default (YOLO) / global config.
/// </summary>
public record SetCliModeRequest
{
    public string CliType { get; init; } = "";
    public string? Mode { get; init; }
}

/// <summary>
/// Body for <c>PUT /api/projects/{name}/cli-context-mode</c>. Sets the
/// per-project context mode for one CLI (T1b / ASS-1742). A null / empty
/// <see cref="Mode"/> clears the override so the CLI reverts to the platform
/// default (CLEAN).
/// </summary>
public record SetCliContextModeRequest
{
    public string CliType { get; init; } = "";
    public string? Mode { get; init; }
}

/// <summary>
/// Body for the project/workspace CLI execution-engine rollout endpoints.
/// Null or blank clears the override; otherwise the value must be
/// <c>car</c> or <c>legacy</c>.
/// </summary>
public record SetCliExecutionEngineRequest
{
    public string? ExecutionEngine { get; init; }
}

public record SetOrchestratorModelRequest
{
    public string? Model { get; init; }
    public string? ThinkingLevel { get; init; }
    public string? Prompt { get; init; }
}

/// <summary>
/// Body for <c>PUT /api/projects/{name}/epic-planning</c>. Tunes the epic
/// decomposition (planning) run: which model authors the sub-task list, and
/// whether the generated sub-tasks land in <c>2-ready</c> instead of
/// <c>0-backlog</c>. Null/absent fields leave that knob on its default.
/// </summary>
public record SetEpicPlanningRequest
{
    public string? Model { get; init; }
    public string? ThinkingLevel { get; init; }
    public bool? SubTasksToReady { get; init; }
}

/// <summary>
/// Body for <c>PUT /api/projects/{name}/autonomy</c>. The integer level is
/// clamped to <c>0..4</c> server-side. See ADR-0026.
/// </summary>
public record SetAutonomyLevelRequest
{
    public int Level { get; init; }
}

/// <summary>
/// Body for <c>PUT /api/projects/{name}/lane-sort-strategy</c> (F35). When
/// <see cref="Strategy"/> is null or empty, the explicit override is cleared
/// and the lane reverts to its default.
/// </summary>
public record SetLaneSortStrategyRequest
{
    public string Lane { get; init; } = "";
    public string? Strategy { get; init; }
}

/// <summary>
/// Body for <c>PUT /api/projects/{projectName}/pipeline-step</c>. Sets the
/// per-project override for one pipeline step. Null fields leave that
/// dimension on its built-in default; an all-null body clears the override.
/// </summary>
public record SetPipelineStepRequest
{
    /// <summary>The pipeline type whose chain is being edited.</summary>
    public string PipelineType { get; init; } = PipelineTypes.Task;
    /// <summary>Full pipeline step id (e.g. <c>aspect-code-quality</c>) or bare suffix (<c>code-quality</c>).</summary>
    public string StepId { get; init; } = "";
    public bool? Enabled { get; init; }
    public bool? EconomyModel { get; init; }
    /// <summary>Bounded loop cap for steps that expose iteration semantics.</summary>
    public int? MaxIterations { get; init; }
    public string? Mode { get; init; }
    public string? CliType { get; init; }
    public string? Model { get; init; }
    public string? ThinkingLevel { get; init; }

    /// <summary>Per-step prompt override (pipeline-admin: editable tool/LLM step
    /// prompts). Null leaves the step's built-in prompt. Added to match the
    /// endpoint that already reads <c>req.Prompt</c> (build break CS1061 otherwise).</summary>
    public string? Prompt { get; init; }

    /// <summary>
    /// Run condition for this step (see <see cref="PipelineStepConditions"/>).
    /// Null leaves the condition on its built-in default; an
    /// <see cref="PipelineStepConditions.Always"/> condition is treated as "no
    /// override" and clears any stored condition.
    /// </summary>
    public PipelineStepCondition? Condition { get; init; }
}

/// <summary>
/// Body for <c>POST /api/runner/{projectName}/orchestrator-log/override</c>.
/// The user is overriding an orchestrator decision: <see cref="OriginalTs"/>
/// names the entry being overridden (timestamp from the feed),
/// <see cref="NewDirection"/> is the new follow-up the user wants applied
/// to <see cref="JobId"/>.
/// </summary>
public record OrchestratorOverrideRequest
{
    public DateTime OriginalTs { get; init; }
    public string JobId { get; init; } = "";
    public string NewDirection { get; init; } = "";
}

public record SetJobModelRequest
{
    public string? Model { get; init; }
}

public record SetJobThinkingLevelRequest
{
    public string? ThinkingLevel { get; init; }
}

public record SetJobCliTypeRequest
{
    public string CliType { get; init; } = "";
    public bool? UseOwnSession { get; init; }
}

public record SetJobTitleRequest
{
    public string Title { get; init; } = "";
}

/// <summary>Body for <c>PUT /api/tasks/{id}/epic</c>: the parent epic id, or null/empty to detach.</summary>
public record SetJobEpicRequest
{
    public string? EpicId { get; init; }
}

public record SetRunnerModeRequest
{
    public string Mode { get; init; } = "manual";

    /// <summary>
    /// Optional cause of the change, threaded into the runner's structured log
    /// and <c>ClassifyModeSource</c>. The UI toggle omits it (defaults to the
    /// operator "api:" reason → source <c>user</c>). The update-service sends
    /// <c>update-quiesce</c> / <c>update-resume</c> so its transient flip to
    /// manual classifies as <c>system</c> and does not overwrite the operator's
    /// durable <see cref="AgentStudio.Shared.ProjectSettings.DesiredRunnerMode"/>
    /// (ASS-1753).
    /// </summary>
    public string? Reason { get; init; }
}

public record SetCliPathRequest
{
    public string Path { get; init; } = "";
}

public record SetGitHubTokenRequest
{
    public string? Token { get; init; }
}

/// <summary>
/// Body for <c>PUT /api/cli/quota/caps</c>. Sets one cap entry by
/// <c>(cliType, windowLabel)</c>; the label matches what the per-CLI quota
/// probe emits (e.g. "Current 5-hour session", "Weekly", "Premium requests").
/// </summary>
public record SetCliQuotaCapRequest
{
    public string CliType { get; init; } = "";
    public string WindowLabel { get; init; } = "";
    public int CapPct { get; init; }
}

public record SetCliModelRouteRequest
{
    public string CliType { get; init; } = "";
    public string? PrimaryModel { get; init; }
    public string? PrimaryThinkingLevel { get; init; }
    public string? FallbackCliType { get; init; }
    public string? FallbackModel { get; init; }
    public string? FallbackThinkingLevel { get; init; }
}
