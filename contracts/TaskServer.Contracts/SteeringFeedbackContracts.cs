namespace AgentStudio.TaskServer.Contracts;

/// <summary>Read-only lifecycle fact derived from the Task Server's authority tables.</summary>
public sealed record SteeringFeedbackReceiptDto(
    string Identity,
    string Kind,
    string State,
    DateTime OccurredAt,
    string TaskId,
    string? CommandId,
    string? AttemptId,
    string? OwningRunId,
    string? IncidentId,
    string Reason,
    bool Current,
    string? SettlementId = null);

public sealed record SteeringFeedbackResponseDto(
    string TaskId,
    string TaskKey,
    string Lane,
    SteeringFeedbackReceiptDto? Current,
    IReadOnlyList<SteeringFeedbackReceiptDto> History);
