namespace AgentStudio.TaskServer.Contracts;

/// <summary>Metadata from a request executed by another host; no credential value crosses the bus.</summary>
public sealed record ProviderProbeComparisonEvidenceDto(
    string Provider, string Service, string RequestShape, string FailureSignature,
    DateTimeOffset LastIndependentSuccessAt, DateTimeOffset ObservedAt,
    bool IndependentCredential, bool ComparableEndpoint,
    string HostId, string CredentialIdentity, bool ExecutedOnComparisonHost);

public sealed record ProviderProbeComparisonResponseDto(
    string? CredentialIdentity, ProviderProbeComparisonEvidenceDto? Comparison);
