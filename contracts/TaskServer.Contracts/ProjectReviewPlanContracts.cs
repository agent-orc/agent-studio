namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// The review plan a project freezes into every review subject the standalone
/// Task Server creates on coding completion (AGT-3061). The plan is server
/// data, like the flow definition: the completion seals it to the fenced
/// result SHA, so a runner never chooses its own review.
/// </summary>
public sealed record ProjectReviewPlanDto(
    string ProjectId,
    long Version,
    string ReviewPolicyHash,
    ReviewPlanDto Plan,
    DateTime UpdatedAt,
    string UpdatedBy);

public sealed record UpsertProjectReviewPlanRequest(
    long? ExpectedVersion,
    string ReviewPolicyHash,
    ReviewPlanDto Plan);
