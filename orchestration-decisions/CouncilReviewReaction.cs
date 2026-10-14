using System.Text.Json.Serialization;
namespace AgentStudio.OrchestrationDecisions;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CouncilFindingAction { FixNextRound, Accept, Escalate }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CouncilReactionDisposition { Accept, Reissue, Escalate }

public sealed record CouncilFindingAssessment(string Finding, CouncilFindingAction Action, string Reason);

/// <summary>The orchestrator's explicit answer to one immutable quality-grade review.</summary>
public sealed record CouncilReviewReaction(
    DateTime CreatedAt,
    string ReviewFileName,
    string Grade,
    CouncilReactionDisposition Disposition,
    string Summary,
    IReadOnlyList<CouncilFindingAssessment> Assessments,
    bool StartsNewRound,
    string? TargetJobId,
    int? TargetRunAttempt);
