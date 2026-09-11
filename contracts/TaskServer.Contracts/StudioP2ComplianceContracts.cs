namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire contracts for the Studio P2 "security review, deployment, publish,
/// and wiki grading" bundle. Security audits, deployment compiles, and
/// publish package/website runs are fenced Runner-dispatch actions backed
/// entirely by <c>studio_operations</c> (see
/// <see cref="StudioOperationKinds"/>); none of these routes materialize a
/// separate results table - GET routes surface the latest completed
/// operation's <see cref="StudioOperationDto.ResultJson"/> as-is. Publish
/// automation is the one plain Task-Server setting in the bundle (a toggle,
/// not an action) and is the only route here with its own storage
/// (<c>studio_publish_settings</c>).
/// </summary>
public sealed record SecurityBaselineDto(string? OperationId, string? ResultJson, DateTime? CompletedAt);

public sealed record SecurityReviewListItemDto(string Id, DateTime? CreatedAt, string? Summary);

public sealed record SecurityReviewListResponse(IReadOnlyList<SecurityReviewListItemDto> Reviews);

/// <summary>
/// A single entry from the latest completed security audit's
/// <c>reviews</c> array, keyed by file name. <see cref="DetailJson"/> is the
/// raw JSON of that entry, passed through unparsed the same way
/// <see cref="StudioOperationDto.ResultJson"/> is - the entry shape is owned
/// by the Runner-side security skill, not the Task Server.
/// </summary>
public sealed record SecurityReviewDetailDto(string FileName, string DetailJson);

public sealed record DeploymentSummaryDto(string? OperationId, string? ResultJson, DateTime? CompletedAt);

public sealed record SetPublishAutomationRequest(bool Enabled);

public sealed record PublishAutomationSettingDto(string ProjectId, bool Enabled, DateTime UpdatedAt);

public sealed record WikiGradingAbortResponse(bool Aborted, string? OperationId);
