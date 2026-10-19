namespace AgentStudio.Secrets;

public sealed record PutSecretRequest(string? Name, string? Value);
public sealed record RotateSecretRequest(string? Value);
public sealed record RevokeSecretRequest(int? Version);

/// <summary>Metadata a person may see. There is no value field on any human-facing response.</summary>
public sealed record SecretMetadataResponse(
    string Reference,
    string Scope,
    string ScopeKind,
    string Name,
    string Status,
    int? CurrentVersion,
    DateTime CreatedAt,
    string CreatedBy,
    DateTime UpdatedAt,
    string UpdatedBy,
    DateTime? RotatedAt,
    string? RotatedBy,
    DateTime? LastUsedAt,
    string? LastUsedBy,
    string? LastUsedPurpose,
    IReadOnlyList<SecretVersionResponse> Versions)
{
    public static SecretMetadataResponse From(SecretRecord record)
    {
        var current = SecretVersionPolicy.CurrentVersion(record);
        return new(
            record.Reference.ToString(), record.Scope, record.ParsedScope.Kind.ToString().ToLowerInvariant(), record.Name,
            current is null ? "revoked" : "active", current,
            record.CreatedAt, record.CreatedBy, record.UpdatedAt, record.UpdatedBy,
            record.RotatedAt, record.RotatedBy, record.LastUsedAt, record.LastUsedBy, record.LastUsedPurpose,
            record.Versions.OrderByDescending(item => item.Version)
                .Select(item => new SecretVersionResponse(item.Version, item.Origin, item.Active, item.CreatedAt, item.CreatedBy,
                    item.RevokedAt, item.RevokedBy, item.LastUsedAt))
                .ToList());
    }
}

public sealed record SecretVersionResponse(
    int Version, string Origin, bool Active, DateTime CreatedAt, string CreatedBy,
    DateTime? RevokedAt, string? RevokedBy, DateTime? LastUsedAt);

public sealed record SecretOverviewResponse(
    string Store,
    string KeySource,
    bool CanWriteWorkspace,
    IReadOnlyList<SecretMetadataResponse> Secrets);

public sealed record SecretAuditResponse(IReadOnlyList<SecretAuditEntry> Entries);

/// <summary>
/// A workload asks for one reference. The workload block names the lease it
/// runs under; the server derives project and environment from that lease,
/// never from the caller's say-so.
/// </summary>
public sealed record ResolveSecretRequest(string? Reference, string? Purpose, SecretWorkloadRequest? Workload);

public sealed record SecretWorkloadRequest(
    string? Kind,
    string? Project = null,
    string? JobId = null,
    string? LeaseToken = null,
    string? TaskKey = null,
    string? LeaseId = null);

public sealed record ResolveSecretResponse(string Reference, string ResolvedReference, int Version, string Value);
