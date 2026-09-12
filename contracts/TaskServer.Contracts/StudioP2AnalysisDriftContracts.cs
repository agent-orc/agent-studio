using System.Text.Json;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire contracts for the P2 "analysis and drift" route bundle.
/// <para>
/// Analysis reports and their schedule are durable Task-Server-owned state:
/// a caller submits a report body directly (no Runner dispatch), so
/// <see cref="AnalysisReportDto.Summary"/> is stored and returned as an
/// opaque <see cref="JsonElement"/> rather than a typed shape.
/// </para>
/// <para>
/// Drift actions are backed by the fenced Runner-operation dispatch ledger
/// (<see cref="StudioOperationDto"/>, see <c>StudioOperationsContracts.cs</c>):
/// a POST creates a durable, claimable operation and returns
/// <see cref="StudioOperationAcceptedResponse"/>; GET routes read the most
/// recent completed operation(s) and surface their opaque
/// <see cref="StudioOperationDto.ResultJson"/> as a <see cref="JsonElement"/>.
/// </para>
/// </summary>
public sealed record AnalysisReportDto(
    string Id,
    string ProjectId,
    string? Trigger,
    string? Severity,
    string? Topic,
    JsonElement Summary,
    DateTime CreatedAt);

/// <summary>Body for <c>POST /api/v1/studio/analysis/{project}/reports</c>.</summary>
public sealed record SubmitAnalysisReportRequest(
    string? Trigger,
    string? Severity,
    string? Topic,
    JsonElement Summary);

public sealed record AnalysisReportListResponse(IReadOnlyList<AnalysisReportDto> Reports);

/// <summary>
/// The analysis schedule settings for a project. <see cref="Schedule"/> and
/// <see cref="UpdatedAt"/> are both null when no schedule has ever been set
/// for the project; GET routes return this default shape rather than 404.
/// </summary>
public sealed record AnalysisScheduleDto(string ProjectId, JsonElement? Schedule, DateTime? UpdatedAt);

/// <summary>Body for <c>PUT /api/v1/studio/analysis/{project}/schedule</c>.</summary>
public sealed record UpdateAnalysisScheduleRequest(JsonElement Schedule);

/// <summary>
/// Response for <c>GET /api/v1/studio/drift/{project}/actions/software-architecture-drift/prompt</c>:
/// a static description of what the dispatched action does. This route is
/// pure (no dispatch, no database read) — it only documents the action.
/// </summary>
public sealed record DriftActionPromptResponse(string Kind, string Description);

/// <summary>Body for <c>POST /api/v1/studio/drift/{project}/architecture/{modelId}/elements/{elementId}/status</c>.</summary>
public sealed record UpdateDriftElementStatusRequest(string Status);

public sealed record DriftElementStatusDto(
    string ProjectId,
    string ModelId,
    string ElementId,
    string Status,
    DateTime UpdatedAt);

/// <summary>
/// The architecture drift surface for a project: the latest completed
/// software-architecture-drift operation's result (if any has ever
/// completed — null fields when none has) overlaid with any stored
/// per-element status overrides.
/// </summary>
public sealed record DriftArchitectureResponse(
    string? SourceOperationId,
    DateTime? GeneratedAt,
    JsonElement? Model,
    IReadOnlyList<DriftElementStatusDto> ElementStatusOverrides);

/// <summary>
/// A drift report as surfaced to Studio: a completed drift-kind studio
/// operation reshaped into the report list/detail shape.
/// <see cref="Summary"/> passes <see cref="StudioOperationDto.ResultJson"/>
/// through as-is; it is null when the operation has not completed yet
/// (should not normally appear in a completed-only listing, but callers
/// reading a single report by id should still be defensive).
/// </summary>
public sealed record DriftReportDto(
    string Id,
    string? ProjectId,
    string Kind,
    string? Trigger,
    string? Severity,
    string? Topic,
    DateTime? CreatedAt,
    JsonElement? Summary);

public sealed record DriftReportListResponse(IReadOnlyList<DriftReportDto> Reports);

/// <summary>
/// Body for <c>POST /api/v1/studio/drift/actions/code-pattern-drift</c>,
/// which — unlike the other three drift actions — carries no
/// <c>{project}</c> route segment; the caller names the project in the body
/// instead.
/// </summary>
public sealed record TriggerCodePatternDriftRequest(string ProjectId);

/// <summary>
/// One entry in the static code-pattern-drift rule catalog returned by
/// <c>GET /api/v1/studio/drift/actions/code-pattern-drift/rules</c>.
/// </summary>
public sealed record DriftRuleDescriptor(string Id, string Title, string Description);

/// <summary>
/// The request payload persisted as a drift studio operation's
/// <c>request_json</c> — just enough to audit which project a dispatched
/// drift action targeted.
/// </summary>
public sealed record DriftDispatchRequest(string ProjectId);
