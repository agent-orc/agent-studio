namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire contracts for the Studio P2 "admin/prompts/utility" bundle: the
/// orchestrator's global config singleton, named prompt overrides (with
/// preview, rebaseline, and review annotation actions), the static
/// component-routing resolver, and the two fenced Runner-dispatch actions
/// (prompt enhancement and title generation). These routes carry no
/// <c>{project}</c> segment; they are global admin/utility surfaces.
/// </summary>
public sealed record UpdateOrchestratorConfigRequest(string ConfigJson);

public sealed record OrchestratorConfigDto(string ConfigJson, DateTime UpdatedAt);

public sealed record UpdatePromptOverrideRequest(string Content);

public sealed record PromptOverrideDto(
    string Name,
    string OverrideContent,
    string? BaselineHash,
    DateTime? LastReviewedAt,
    string? ReviewSummaryJson,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record PreviewPromptOverrideRequest(string? Content = null);

public sealed record PreviewPromptOverrideResponse(string Name, string EffectiveContent);

public sealed record RebaselinePromptOverrideResponse(string Name, string BaselineHash, DateTime UpdatedAt);

public sealed record PromptReviewAnnotationDto(
    string Name,
    int ContentLength,
    int LineCount,
    DateTime LastReviewedAt);

public sealed record ReviewAllPromptsResponse(IReadOnlyList<PromptReviewAnnotationDto> Reviewed);

public sealed record ResolveComponentRoutingRequest(string Path);

public sealed record ResolveComponentRoutingResponse(string OwningComponent, string FrontendOwner);

public sealed record EnhancePromptRequest(string Prompt);

public sealed record GenerateTitleRequest(string Context);
