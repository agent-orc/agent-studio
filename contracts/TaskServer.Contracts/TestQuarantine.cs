namespace AgentStudio.TaskServer.Contracts;

/// <summary>RT-4 server entry. Repository-file entries are projected as owner report-only entries.</summary>
public sealed record ProjectTestQuarantine(
    string Id, string Test, string Card, DateOnly ExpiresOn, DateOnly? ResolvedOn, string? Reason,
    string Kind, string StartedBy, DateTimeOffset StartedAtUtc, DateTimeOffset ExpiresAtUtc,
    string IncidentFingerprint, IReadOnlyList<string> EvidenceRunIds, IReadOnlyList<string> CoveringFolders,
    int Renewals = 0, string? LiftedBy = null, IReadOnlyList<string>? LiftEvidence = null,
    DateTimeOffset? LiftedAtUtc = null, string Source = "store")
{
    public string? SuiteId { get; init; }
    public string? TestFile { get; init; }
    public IReadOnlyList<string> SuiteAreas { get; init; } = [];
    public IReadOnlyList<string> ConfirmedSuspectPaths { get; init; } = [];
    public string? AttributedRevertCardId { get; init; }
    public IReadOnlyList<string> OwnerExemptCardIds { get; init; } = [];
}

/// <summary>One immutable incident decision with the PersonaLogEntryV1 decision fields.</summary>
public sealed record PostMergeIncidentLogEntry(
    string DecisionId, string Rule, string Revision, string FactsDigest, string Class, double? Confidence,
    IReadOnlyList<string> Actions, IReadOnlyList<string> HeldCards, string LiftCondition, string Reason,
    string RunId, string Sha, string Test, string IncidentFingerprint, string Principal, DateTimeOffset AtUtc);

public sealed record TestQuarantineAudit(string Id, string QuarantineId, string Operation, string Actor,
    DateTimeOffset AtUtc, ProjectTestQuarantine? Before, ProjectTestQuarantine After);

/// <summary>RT-5 hold facts, shared with integration and the future dispatcher pick gate.</summary>
public sealed record TestQuarantineHoldFact(string QuarantineId, string Test, string RepairCardId,
    IReadOnlyList<string> CoveringFolders, IReadOnlyList<string> Files, IReadOnlyList<string> SuiteAreas,
    DateTimeOffset ExpiresAtUtc, string? AttributedRevertCardId, IReadOnlyList<string> OwnerExemptCardIds);
