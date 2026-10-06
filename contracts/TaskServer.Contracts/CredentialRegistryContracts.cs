using System.Text.Json.Serialization;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>Only metadata crosses this boundary. Secret material stays with its host adapter.</summary>
public static class CredentialRegistryProtocol
{
    public const int SchemaVersion = 1;
    public static readonly IReadOnlyDictionary<string, string> RenewalMethods =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["claude_oauth_token"] = "R1", ["claude_native_login"] = "R2",
            ["codex_chatgpt_login"] = "R3", ["github_https_token"] = "R4",
            ["github_provisioning_oauth"] = "R4", ["github_deploy_key"] = "R5",
            ["task_server_principal"] = "R6", ["wireguard_peer"] = "R7",
            ["provider_api_key"] = "R8", ["administration_ssh_key"] = "R9",
        };
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "claude_oauth_token", "claude_native_login", "codex_chatgpt_login",
        "github_https_token", "github_provisioning_oauth", "github_deploy_key",
        "task_server_principal", "wireguard_peer", "provider_api_key",
        "administration_ssh_key",
    };
    public static readonly IReadOnlySet<string> Adapters = new HashSet<string>(StringComparer.Ordinal)
    {
        "credential-helper", "environment-file", "native-cli-store", "network-key",
        "service-secret", "ssh-private-key", "external-vault", "docker-secret",
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CredentialBindingDto(string ServiceId, string Purpose, string SourceRef);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CredentialLocatorDto(string Adapter, string LocalRef, string EffectiveSource);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CredentialRegistryRecordDto(
    string InstallationId,
    string HostId,
    string CredentialId,
    string Provider,
    string Owner,
    string RecoveryOwner,
    string Generation,
    string RenewalMethod,
    int SchemaVersion,
    string Kind,
    string? AccountRef,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<CredentialBindingDto> Bindings,
    CredentialLocatorDto Locator,
    string ExpiryKnowledge,
    IReadOnlyDictionary<string, string> UnknownReasons,
    string? Supersedes,
    string? PublicFingerprint,
    string? LastRenewedBy,
    string? RunbookId,
    string? LastOperationId,
    string LastOutcome,
    IReadOnlyList<string> EvidenceRefs,
    DateTime? CreatedAt,
    DateTime? DiscoveredAt,
    DateTime? LastVerifiedAt,
    DateTime? LastRenewedAt,
    DateTime? ExpiresAt,
    DateTime? RotationDueAt,
    DateTime? AccessTokenExpiresAt,
    DateTime? LastRealSuccessAt,
    DateTime? NextProbeAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? GitHubKeyId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProvisioningCredentialId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RepositoryPurpose = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? RepositoryWriteGrant = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TokenSubtype = null);

/// <summary>Expected generation and source instance fence an observation before storage.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CredentialRegistryObservationRequest(
    CredentialRegistryRecordDto Record,
    string SourceInstanceId,
    string? ExpectedGeneration,
    DateTime ObservedAt);
