namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// One task file as the workspace holds it. <see cref="ContentBase64"/> is
/// left out on the first pass: the Task Server answers which paths it needs,
/// so unchanged content never crosses the wire twice.
/// </summary>
public sealed record TaskFileMirrorEntry(
    string Path,
    string Sha256,
    long SizeBytes,
    string? MediaType = null,
    string? ContentBase64 = null);

public sealed record TaskFileMirrorTask(
    string TaskKey,
    IReadOnlyList<TaskFileMirrorEntry>? Files,
    IReadOnlyList<string>? Removed = null);

/// <summary>
/// Task files of one project copied from the workspace while the workspace is
/// still the write authority (pipeline delivery target, section 05
/// "Task-folder artefacts", AGT-3320). Keyed by task key and relative path.
/// </summary>
public sealed record TaskFileMirrorRequest(IReadOnlyList<TaskFileMirrorTask>? Tasks);

public sealed record TaskFileMirrorNeed(string TaskKey, string Path);

public sealed record TaskFileMirrorResponse(
    string ProjectId,
    int Written,
    int Unchanged,
    int Removed,
    IReadOnlyList<TaskFileMirrorNeed> Needed,
    IReadOnlyList<BoardImportRejection> Rejected);

/// <summary>A card with a verified integration record and the commits it names.</summary>
public sealed record IntegratedCardDto(
    string TaskKey,
    string Title,
    string State,
    IReadOnlyList<string> Shas,
    string IntegrationBranch);

/// <summary>Release card attribution input, answered from Task Server rows instead of a task-folder scan.</summary>
public sealed record IntegratedCardsResponse(string ProjectId, IReadOnlyList<IntegratedCardDto> Cards);
