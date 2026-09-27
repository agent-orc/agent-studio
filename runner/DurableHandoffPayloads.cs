namespace AgentRunner;

public sealed record DurableArtifactPayload(
    string Name,
    string MediaType,
    string ContentBase64,
    string Sha256);

public sealed record DurableCompletionPayload(
    string Outcome,
    string? Summary,
    string? ResultEnvelopeDigest,
    AgentStudio.TaskServer.Contracts.ExecutionOutcomeDecision? OutcomeDecision = null,
    string? NeedsInputMessage = null,
    string? SalvageBranch = null,
    string? SalvageCommitSha = null,
    // AGT-2820: the incident lines this completion carries. Journalled with the
    // payload so a replayed or recovered outbox item reports the same incident.
    IReadOnlyList<string>? GateItems = null,
    AgentStudio.TaskServer.Contracts.SessionContinuationLedgerEntry? SessionContinuation = null);

public sealed record DurableRunContextPayload(
    string RepositoryId,
    string? RepositoryUrl,
    string? DefaultBranch,
    string BaseSha);

public sealed record DurableGitFactsPayload(
    string RepositoryId,
    string BaseSha,
    string ResultSha,
    string? ImmutableResultRef,
    SalvageReconciliationResult? SalvageReconciliation,
    string? RecoveryAction);

public sealed record DurableTerminalPayload(
    string Outcome,
    string? Reason);

public sealed record ArtifactManifestEntry(
    string Path,
    string Sha256,
    long SizeBytes);

public sealed record DurableArtifactManifest(
    string Digest,
    string Json);
