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
    string? SalvageBranch = null);

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
