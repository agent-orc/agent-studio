namespace AgentStudio.TaskServer.Contracts;

/// <summary>Redacted facts only. Unknown facts stay null and cannot authorize repair.</summary>
public sealed record CredentialIncidentFacts(
    string CredentialKind,
    string ProbeOutcome,
    bool? RefreshSessionSharedAcrossHosts,
    string? NormalizedRequestCode,
    string? IncidentCorroboration,
    string? RepositoryPurpose,
    bool? RepositoryRequiredAccessVerified,
    bool? RepositoryRequiresPush = null);

public sealed record CredentialRunbookSelection(string RunbookId, int Version, string Policy);

public sealed record BeginCredentialRunbookRequest(
    string OperationId,
    string HostId,
    string CredentialId,
    string ExpectedGeneration,
    string IncidentId,
    string EvidenceCorrelationId,
    CredentialIncidentFacts Facts);

public sealed record CredentialRunbookOperation(
    string OperationId,
    string RunbookId,
    int Version,
    string Policy,
    string ActorId,
    string HostId,
    string CredentialId,
    string ExpectedGeneration,
    string IncidentId,
    string EvidenceCorrelationId,
    string Status,
    DateTime CreatedAt);

public sealed record CredentialRunbookStepClaim(
    string Status,
    string? StepId,
    string? OperationId,
    string? HostId,
    string? ExpectedGeneration);

public sealed record CompleteCredentialRunbookStepRequest(
    string HostId,
    string ExpectedGeneration,
    string Outcome,
    string? ObservedGeneration,
    IReadOnlyList<string> EvidenceRefs,
    string? InstanceId = null,
    string? VerificationResult = null);

public sealed record CredentialRunbookStepReceipt(
    string OperationId,
    string StepId,
    string ActorId,
    string HostId,
    string CredentialId,
    string ExpectedGeneration,
    string? ObservedGeneration,
    string Outcome,
    IReadOnlyList<string> EvidenceRefs,
    DateTime OccurredAt,
    string? IncidentId = null,
    string? EvidenceCorrelationId = null,
    string? ExecutingActorId = null,
    string? VerificationResult = null);
