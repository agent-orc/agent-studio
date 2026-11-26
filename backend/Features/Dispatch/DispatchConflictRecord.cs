namespace AgentStudio.Dispatch;

/// <summary>Immutable conflict evidence. Admission attribution stays unknown until DS-6 supplies it.</summary>
public sealed record DispatchConflictRecordV1(
    string Id, string Project, string Card, string Kind, string DeliveryBaseSha,
    string IntegrationTipSha, IReadOnlyList<string> ConflictedFiles,
    IReadOnlyList<string> PartnerCards, DateTimeOffset At)
{
    public int Version { get; init; } = 1;
    public IReadOnlyDictionary<string, string?> DispatchDecisionIds { get; init; } = new Dictionary<string, string?>();
    public string? PersonaId { get; init; }
    public string? PersonaMode { get; init; }
    public bool Backfilled { get; init; }
    public string? EvidenceId { get; init; }
}
