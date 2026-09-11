using System.Text.Json;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire contracts for the P2 "design council, proposals, visual evidence,
/// skill readiness, project snapshot" route bundle.
/// <para>
/// Design actions are backed by the fenced Runner-operation dispatch ledger
/// (<see cref="StudioOperationDto"/>, see <c>StudioOperationsContracts.cs</c>):
/// a POST creates a durable, claimable <see cref="StudioOperationKinds.DesignAction"/>
/// operation and returns <see cref="StudioOperationAcceptedResponse"/>; GET
/// routes read the most recently completed operation for the project and
/// surface a slice of its opaque <see cref="StudioOperationDto.ResultJson"/>
/// as a <see cref="JsonElement"/>, defensively defaulting to an empty shape
/// when nothing has completed yet.
/// </para>
/// <para>
/// Proposals (<c>studio_proposals</c>) are a durable Task-Server-owned list
/// of independently actionable rows, materialized by
/// <c>ProposalsCompletionProjector</c> once a
/// <see cref="StudioOperationKinds.ProposalsGenerate"/> or
/// <see cref="StudioOperationKinds.ProposalsRefineFeedback"/> operation
/// succeeds. The projector's expected <see cref="StudioOperationDto.ResultJson"/>
/// shape is <c>{ "proposals": [ { ... }, ... ] }</c>; each array entry is
/// stored as-is in <see cref="StudioProposalDto.Payload"/>.
/// </para>
/// <para>
/// Visual evidence (<c>studio_visual_evidence</c>) and design council
/// acceptance (<c>studio_design_council_acceptance</c>) are plain
/// Task-Server-owned tables with no dispatch involved.
/// </para>
/// </summary>
public sealed record DesignActionDispatchRequest(JsonElement? Payload = null);

/// <summary>
/// The request payload persisted as a design studio operation's
/// <c>request_json</c> — enough to audit which project and action a
/// dispatched design action targeted.
/// </summary>
public sealed record DesignDispatchRequest(string ProjectId, string Action, JsonElement? Payload);

/// <summary>
/// List-shaped slice of the latest completed <see cref="StudioOperationKinds.DesignAction"/>
/// result's <c>council</c> array. <see cref="SourceOperationId"/> and
/// <see cref="GeneratedAt"/> are null and <see cref="Council"/> is empty when
/// no design action has ever completed for the project.
/// </summary>
public sealed record DesignCouncilListResponse(
    string? SourceOperationId,
    DateTime? GeneratedAt,
    IReadOnlyList<JsonElement> Council);

/// <summary>Body for <c>POST /api/v1/studio/projects/{project}/design/council/{fileName}/accept</c>. No fields; the file is named in the route.</summary>
public sealed record DesignCouncilAcceptanceDto(string ProjectId, string FileName, DateTime AcceptedAt, string AcceptedBy);

/// <summary>
/// Slice of the latest completed design action result's <c>overview</c>
/// field. <see cref="Overview"/> is null when no design action has ever
/// completed for the project.
/// </summary>
public sealed record DesignOverviewResponse(string? SourceOperationId, DateTime? GeneratedAt, JsonElement? Overview);

/// <summary>
/// Slice of the latest completed design action result's <c>references</c>
/// array. Empty when no design action has ever completed for the project.
/// </summary>
public sealed record DesignReferencesResponse(
    string? SourceOperationId,
    DateTime? GeneratedAt,
    IReadOnlyList<JsonElement> References);

/// <summary>Status values a <see cref="StudioProposalDto"/> row can carry.</summary>
public static class StudioProposalStatuses
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
}

/// <summary>
/// One durable, independently actionable proposal row. <see cref="Payload"/>
/// passes the generator's per-proposal JSON through as-is; the Task Server
/// does not interpret its shape beyond the enclosing envelope described on
/// <see cref="StudioProposalStatuses"/>.
/// </summary>
public sealed record StudioProposalDto(
    string Id,
    string ProjectId,
    long Generation,
    string Status,
    JsonElement Payload,
    DateTime CreatedAt,
    DateTime? DecidedAt);

public sealed record StudioProposalListResponse(IReadOnlyList<StudioProposalDto> Proposals);

/// <summary>Body for <c>POST /api/v1/studio/projects/{project}/proposals/{proposalId}/decision</c>. <see cref="Decision"/> must be <c>"accepted"</c> or <c>"rejected"</c>.</summary>
public sealed record DecideProposalRequest(string Decision);

/// <summary>Body for <c>POST /api/v1/studio/projects/{project}/proposals/refine-feedback</c>, also persisted as-is as the dispatched operation's <c>request_json</c>.</summary>
public sealed record RefineProposalsFeedbackRequest(string Feedback);

/// <summary>
/// The request payload persisted as a <see cref="StudioOperationKinds.ProposalsGenerate"/>
/// operation's <c>request_json</c>.
/// </summary>
public sealed record ProposalsDispatchRequest(string ProjectId);

/// <summary>Body for <c>POST /api/v1/studio/projects/{project}/skill-readiness/fix-task</c>, also persisted as-is as the dispatched operation's <c>request_json</c>.</summary>
public sealed record FixSkillReadinessTaskRequest(string TaskId);

/// <summary>
/// Task counts by board lane for one project, pulled from the durable
/// <c>tasks</c> table. See <see cref="StudioTaskLanes"/> for the lane
/// constants each count corresponds to.
/// </summary>
public sealed record StudioProjectQueueSummaryDto(
    int Backlog,
    int Ready,
    int Progress,
    int AutoReview,
    int HumanReview,
    int Escalated,
    int Completed,
    int Archive,
    int Total);

/// <summary>
/// The Task-Server-owned half of the project snapshot surface: the resolved
/// project plus its queue summary. Per the route's ownership caveat, this
/// deliberately excludes any local checkout path or repository status; a
/// caller that needs those joins them from a separately fetched dev-seat
/// projection.
/// </summary>
public sealed record StudioProjectSnapshotResponse(ProjectDto Project, StudioProjectQueueSummaryDto Queue, DateTime GeneratedAt);

/// <summary>
/// One durable visual-evidence item (for example a captured screenshot and
/// its critique). <see cref="Payload"/> is opaque; <see cref="AcknowledgedAt"/>
/// is null until a caller acknowledges the item.
/// </summary>
public sealed record StudioVisualEvidenceItemDto(
    string Id,
    string ProjectId,
    DateTime CapturedAt,
    JsonElement Payload,
    DateTime? AcknowledgedAt);

public sealed record StudioVisualEvidenceListResponse(IReadOnlyList<StudioVisualEvidenceItemDto> Items);
