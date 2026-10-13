using System.Text.Json.Serialization;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>Metadata-only provider renewal. Browser codes and secret values are never receipts.</summary>
public static class ProviderRenewalProtocol
{
    public static readonly IReadOnlySet<string> Methods = new HashSet<string>(StringComparer.Ordinal)
    {
        "R1", "R2", "R3", "R8",
    };

    public static readonly string[] Steps =
    [
        "requested", "preflight", "awaiting-human", "staged", "installed",
        "verified", "retired", "complete",
    ];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BeginProviderRenewalRequest(
    string InstallationId,
    string HostId,
    string CredentialId,
    string ExpectedGeneration,
    string Method,
    string IdempotencyKey,
    DateTime Deadline);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdvanceProviderRenewalRequest(
    string Step,
    string? ObservedGeneration,
    string? EffectiveSource,
    IReadOnlyList<string> VerifiedUnits,
    bool RealRequestSucceeded,
    IReadOnlyList<string> EvidenceRefs);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProviderRenewalReceiptDto(
    string OperationId,
    string InstallationId,
    string HostId,
    string CredentialId,
    string ExpectedGeneration,
    string Method,
    string IdempotencyKey,
    string ActorId,
    DateTime Deadline,
    string Step,
    string? ObservedGeneration,
    string? EffectiveSource,
    IReadOnlyList<string> VerifiedUnits,
    bool RealRequestSucceeded,
    IReadOnlyList<string> EvidenceRefs,
    DateTime UpdatedAt);
