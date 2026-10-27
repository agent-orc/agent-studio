namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Card core fields of the per-project board read model (pipeline delivery
/// target, section 05, M1 and M2). Only the fields the board lays out are
/// carried; detail facts stay on the task routes.
/// </summary>
public sealed record BoardCardDto(
    string TaskId,
    string TaskKey,
    string Title,
    string State,
    long Rank,
    long Version,
    string? ArchiveState,
    DateTime UpdatedAt);

public sealed record BoardLaneDto(string State, int Count, IReadOnlyList<BoardCardDto> Cards);

/// <summary>
/// One project's board answered from the <c>tasks</c> table. <see cref="Generation"/>
/// is the change-feed cursor of the last committed board change of the project
/// and names the representation: equal generations mean an equal board.
/// <see cref="WorkspaceSourceGeneration"/> is the workspace index generation
/// of the last shadow import, null when the project was never imported.
/// </summary>
public sealed record BoardReadModelResponse(
    string ProjectId,
    string ProjectName,
    long Generation,
    string ETag,
    IReadOnlyList<BoardLaneDto> Lanes,
    int LiveCount,
    int ArchivedCount,
    long? WorkspaceSourceGeneration);

public static class BoardChangeKinds
{
    public const string Upsert = "upsert";
    public const string Remove = "remove";
}

public sealed record BoardChangeDto(
    long Generation,
    string TaskId,
    string TaskKey,
    string Change,
    string? State);

/// <summary>
/// Board changes of one project after a generation. <see cref="ResyncRequired"/>
/// is true when changes after <see cref="After"/> were pruned or the cursor is
/// ahead of the server; the client then reads the board again.
/// </summary>
public sealed record BoardChangeFeedResponse(
    string ProjectId,
    long After,
    long Generation,
    bool ResyncRequired,
    IReadOnlyList<BoardChangeDto> Changes);

/// <summary>Card core of one workspace task as the monolith's task index publishes it.</summary>
public sealed record BoardWorkspaceCard(string TaskKey, string Title, string State, long Rank);

/// <summary>
/// Shadow import of workspace card core into the Task Server while the
/// workspace is still the write authority (M1 and M2). Upserts and removals
/// are keyed by task key; <see cref="SourceGeneration"/> is the workspace index
/// generation the batch was taken from.
/// </summary>
public sealed record BoardWorkspaceImportRequest(
    long SourceGeneration,
    IReadOnlyList<BoardWorkspaceCard>? Upserts,
    IReadOnlyList<string>? Removals);

public sealed record BoardImportRejection(string TaskKey, string Code, string Message);

public sealed record BoardWorkspaceImportResponse(
    string ProjectId,
    long Generation,
    long SourceGeneration,
    int Inserted,
    int Updated,
    int Unchanged,
    int Removed,
    IReadOnlyList<BoardImportRejection> Rejected);
