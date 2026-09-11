namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire contracts for the Studio P1 "workspace" bundle (G9_Workspace):
/// workspace-wide aggregates computed live across the whole Task Server data
/// root (<c>/api/v1/studio/workspace/...</c>, singular) and per-row CRUD /
/// extension settings on the existing <c>workspaces</c> table
/// (<c>/api/v1/studio/workspaces/{id}/...</c>, plural). The aggregate routes
/// accept an optional <c>workspaceId</c> query filter; when omitted they fold
/// together every workspace, matching the schema's support for more than one
/// even though a real deployment normally has exactly one.
/// </summary>
public sealed record UpdateWorkspaceRequest(string Name, long ExpectedVersion);

public sealed record WorkspaceDeleteResponse(string WorkspaceId, bool Deleted);

public sealed record ReorderWorkspacesRequest(IReadOnlyList<string> WorkspaceIds);

public sealed record WorkspaceRankDto(string WorkspaceId, long Rank);

public sealed record ReorderWorkspacesResponse(IReadOnlyList<WorkspaceRankDto> Workspaces);

/// <summary>
/// Free-form autonomy toggles for unattended task progression within a
/// workspace. The legacy monolith's exact field set is not part of this
/// migration's contract surface; this shape covers the settings every P1
/// autonomy route needs to round-trip (on/off, how far to auto-advance, and
/// how many runs may proceed unattended at once) and is intentionally
/// additive - new fields can be appended without breaking stored rows, since
/// the whole object is persisted as one JSON blob.
/// </summary>
public sealed record WorkspaceAutonomySettings(
    bool? AutoStartTasks = null,
    bool? AutoAdvanceOnReviewPass = null,
    int? MaxConcurrentAutoRuns = null,
    string? Mode = null);

public sealed record UpdateWorkspaceAutonomyRequest(WorkspaceAutonomySettings Autonomy, long ExpectedVersion);

public sealed record UpdateWorkspaceOrchestratorModelRequest(string? OrchestratorModel, long ExpectedVersion);

/// <summary>
/// The full <c>workspace_studio_settings</c> row for one workspace. Returned
/// with <c>Version == 0</c> and every field <see langword="null"/> when no
/// row has been written yet (autonomy, orchestrator model, and the generic
/// settings blob are all upserted lazily on first write).
/// </summary>
public sealed record WorkspaceStudioSettingsDto(
    string WorkspaceId,
    WorkspaceAutonomySettings? Autonomy,
    string? OrchestratorModel,
    string? SettingsJson,
    long Version,
    DateTime UpdatedAt);

/// <summary>
/// Workspace-wide counts. <see cref="WorkspaceId"/> is <see langword="null"/>
/// when the request did not supply a <c>workspaceId</c> filter, meaning the
/// counts are folded across every workspace in the data root.
/// </summary>
public sealed record WorkspaceSummaryDto(
    string? WorkspaceId,
    int ProjectCount,
    IReadOnlyDictionary<string, int> TaskCountsByState,
    int RunCount,
    int ActiveRunnerCount,
    DateTime GeneratedAt);

public sealed record WorkspaceScreenshotDto(
    string ArtifactId,
    string RunId,
    string TaskId,
    string ProjectId,
    string Name,
    string MediaType,
    long SizeBytes,
    DateTime CreatedAt);

public sealed record WorkspaceScreenshotsResponse(IReadOnlyList<WorkspaceScreenshotDto> Screenshots);

/// <summary>
/// One orchestrator context (a project or a specific task within it) ranked
/// by total token usage across every recorded turn.
/// </summary>
public sealed record WorkspaceExpensiveJobDto(
    string ContextKey,
    string Kind,
    string ProjectId,
    string? TaskId,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    long TotalTokens,
    DateTime LastUsedAt);

public sealed record WorkspaceExpensiveJobsResponse(IReadOnlyList<WorkspaceExpensiveJobDto> Jobs, DateTime GeneratedAt);

public sealed record WorkspaceTokenTimelineBucketDto(
    string Date,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    long TotalTokens);

public sealed record WorkspaceTokenTimelineResponse(IReadOnlyList<WorkspaceTokenTimelineBucketDto> Buckets, DateTime GeneratedAt);
