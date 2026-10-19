
namespace AgentStudio.TaskAccess;

/// <summary>
/// Optimistic-concurrency token for one indexed job. A monotonic
/// version counter combined with the file mtime that produced it.
/// Consumers that read-then-write hand the token back so the layer
/// can reject a stale write with <see cref="TaskMutationStatus.Conflict"/>.
/// </summary>
public record TaskAccessVersion(long Version, DateTime Mtime);

/// <summary>
/// Snapshot of the layer's view at a point in time. Returned by
/// <see cref="ITaskAccess.Snapshot"/> so external consumers (Layer 3
/// review, companion app, future HTTP relay) can read a single
/// coherent picture without touching disk.
/// </summary>
public record TaskAccessSnapshot
{
    public DateTime CapturedAt { get; init; }
    public long Version { get; init; }
    public IReadOnlyList<TaskInfo> Jobs { get; init; } = [];
}

/// <summary>
/// Mutation request against one job record. Intentionally narrow:
/// each known mutation gets its own typed kind, so the layer can
/// enforce single-state-machine authority on lane moves separately
/// from field edits and prompt timeline appends.
/// </summary>
public record TaskMutationRequest
{
    public string JobId { get; init; } = "";
    public string? WatchPath { get; init; }
    public TaskMutationKind Kind { get; init; }
    public string? FieldName { get; init; }
    public string? FieldValue { get; init; }
    public string? PromptMarkdown { get; init; }
    public string? LogLine { get; init; }
    public TaskAccessVersion? ExpectedVersion { get; init; }

    /// <summary>
    /// Carrier for the <see cref="TaskMutationKind.Create"/> path so the
    /// layer can mint a new job folder without a bespoke method. Ignored
    /// for other kinds.
    /// </summary>
    public CreateTaskRequest? CreateRequest { get; init; }
}

public enum TaskMutationKind
{
    UpdateField,
    AttachPrompt,
    AppendLogLine,
    Create,
}

/// <summary>
/// Lane transition request. Routed through the existing
/// <c>TaskStateMachine</c> from inside the layer so the
/// "one running task per project" invariant stays in one place.
/// </summary>
public record TaskTransitionRequest
{
    public string JobId { get; init; } = "";
    public string? WatchPath { get; init; }
    public string TargetLane { get; init; } = "";
    /// <summary>Ledger actor of the lane change (who triggered it).</summary>
    public string? Cause { get; init; }
    public string? Reason { get; init; }
    /// <summary>Ledger cause id of the lane change (why), one of <see cref="LaneChangeCauses"/>.</summary>
    public string? TransitionCause { get; init; }
    /// <summary>Short qualifier for <see cref="TransitionCause"/>.</summary>
    public string? TransitionDetail { get; init; }
    public TaskAccessVersion? ExpectedVersion { get; init; }
}

public record TaskMutationResult
{
    public TaskMutationStatus Status { get; init; }
    public TaskInfo? Job { get; init; }
    public TaskAccessVersion? Version { get; init; }
    public string? Message { get; init; }
}

public enum TaskMutationStatus
{
    Applied,
    NotFound,
    Conflict,
    Rejected,
}

/// <summary>
/// Change notification delivered to a project subscriber. Carries
/// enough context for the runner, supervisor, and SignalR hub to
/// react without rescanning.
/// </summary>
public record TaskChange
{
    public DateTime At { get; init; }
    public string ProjectName { get; init; } = "";
    public string JobId { get; init; } = "";
    public TaskChangeKind Kind { get; init; }
    public string? FromLane { get; init; }
    public string? ToLane { get; init; }
    public TaskAccessVersion? Version { get; init; }
}

public enum TaskChangeKind
{
    Created,
    Updated,
    Transitioned,
    Deleted,
}

/// <summary>
/// Lightweight (watchPath, lane, slug, folderPath) tuple returned by
/// <see cref="ITaskAccess.ListLaneFolders"/>. <c>FolderPath</c> may point to a
/// flat <c>tasks/&lt;bucket&gt;/&lt;key&gt;</c> task or a legacy lane folder. Used by
/// orphan-rescue paths that need an absolute folder path to read its contents
/// (logs, task.json mtime) without resolving the storage layout themselves.
/// </summary>
public record LaneFolderRef
{
    public string WatchPath { get; init; } = "";
    public string Lane { get; init; } = "";
    public string Slug { get; init; } = "";
    public string FolderPath { get; init; } = "";
}

/// <summary>
/// One folder entry observed by
/// <see cref="ITaskAccess.ListAllLaneFolders"/>. Includes folders
/// without a <c>task.json</c> so the queue-health endpoint can flag
/// orphans without reaching into the filesystem from outside the
/// layer.
/// </summary>
public record LaneFolderEntry
{
    public string WatchPath { get; init; } = "";
    public string Lane { get; init; } = "";
    public string Slug { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public bool HasJobJson { get; init; }
    /// <summary>
    /// <c>state</c> field read from <c>task.json</c> when present, used by
    /// the queue-health endpoint to detect lane / state-field drift.
    /// Null when <c>task.json</c> is missing or unreadable.
    /// </summary>
    public string? StateInJobJson { get; init; }
}
