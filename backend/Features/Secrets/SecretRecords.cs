using System.Text.Json.Serialization;

namespace AgentStudio.Secrets;

/// <summary>
/// Metadata of one secret. It never carries a value; values live only in the
/// store behind <see cref="ISecretStore.ReadValueAsync"/>.
/// </summary>
public sealed record SecretRecord
{
    public required string Id { get; init; }
    public required string Scope { get; init; }
    public required string Name { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required string CreatedBy { get; init; }
    public required DateTime UpdatedAt { get; init; }
    public required string UpdatedBy { get; init; }
    public DateTime? RotatedAt { get; init; }
    public string? RotatedBy { get; init; }
    public DateTime? LastUsedAt { get; init; }
    public string? LastUsedBy { get; init; }
    public string? LastUsedPurpose { get; init; }
    /// <summary>The current item revision in an external store; metadata updates may advance it without adding a value version.</summary>
    public int? StoreRevision { get; init; }
    public List<SecretVersionRecord> Versions { get; init; } = [];

    [JsonIgnore]
    public SecretScope ParsedScope => SecretScope.Parse(Scope);
    [JsonIgnore]
    public SecretReference Reference => new(ParsedScope, Name);
}

/// <summary>Every stored secret the store could verify, plus the items it could not.</summary>
public sealed record SecretListing(IReadOnlyList<SecretRecord> Records, IReadOnlyList<UnreadableSecret> Unreadable);

/// <summary>
/// A stored item whose metadata failed its key or integrity check. One such
/// item does not hide the others. <see cref="Scope"/> and <see cref="Name"/>
/// are the unverified claims of the payload, for orientation only.
/// </summary>
public sealed record UnreadableSecret(string Id, string? Scope, string? Name, string Problem, string Reason)
{
    public const string KeyUnavailable = "key-unavailable";
    public const string IntegrityFailed = "integrity-failed";
    public const string ReadFailed = "read-failed";
}

public sealed record SecretVersionRecord
{
    public required int Version { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required string CreatedBy { get; init; }
    /// <summary><c>put</c> or <c>rotate</c>.</summary>
    public required string Origin { get; init; }
    public DateTime? RevokedAt { get; init; }
    public string? RevokedBy { get; init; }
    public DateTime? LastUsedAt { get; init; }

    [JsonIgnore]
    public bool Active => RevokedAt is null;
}

/// <summary>Who acts on a secret. Humans write and read metadata; workloads resolve.</summary>
public sealed record SecretActor(SecretActorKind Kind, string Id)
{
    public static SecretActor Human(string id) => new(SecretActorKind.Human, id);
    public static SecretActor ServiceAccount(string id) => new(SecretActorKind.ServiceAccount, id);
    public static SecretActor Workload(string id) => new(SecretActorKind.Workload, id);
    public override string ToString() => Kind switch
    {
        SecretActorKind.Human => "human:" + Id,
        SecretActorKind.ServiceAccount => "service-account:" + Id,
        _ => "workload:" + Id,
    };
}

public enum SecretActorKind
{
    Human,
    ServiceAccount,
    Workload
}

/// <summary>
/// The run-time identity a value is issued to, already bound by the caller to
/// a verified lease: a deploy job for one project environment, or a task run
/// for one project. <see cref="DeclaredNames"/> narrows a deploy job to the
/// secret names its environment declaration lists.
/// </summary>
public sealed record SecretWorkload(
    string Kind,
    string Identity,
    string Project,
    string? Environment,
    IReadOnlyCollection<string>? DeclaredNames = null)
{
    public const string DeployJob = "deploy-job";
    public const string TaskRun = "task-run";

    public SecretActor Actor => SecretActor.Workload(Identity);
}

public sealed record ResolvedSecret(SecretReference Requested, SecretReference Resolved, string Value);

public sealed record SecretAuditEntry
{
    public required DateTime At { get; init; }
    public required string Action { get; init; }
    public required string Actor { get; init; }
    public required string Scope { get; init; }
    public required string Reference { get; init; }
    public required string Outcome { get; init; }
    public int? Version { get; init; }
    public string? Purpose { get; init; }
    public string? Workload { get; init; }
    /// <summary>Set on a merged refusal entry: how many identical refusals it stands for; <see cref="At"/> is the last of them.</summary>
    public int? Repeated { get; init; }
}

public static class SecretAuditActions
{
    public const string Put = "put";
    public const string Rotate = "rotate";
    public const string Revoke = "revoke";
    public const string Delete = "delete";
    public const string Resolve = "resolve";
}

public sealed class SecretNotFoundException(string reference)
    : SecretValidationException("secret-not-found", $"No secret at {reference}.");

public sealed class SecretRevokedException(string reference)
    : SecretValidationException("secret-revoked", $"{reference} is revoked.");

public sealed class SecretAccessDeniedException(string code, string message) : SecretValidationException(code, message);

/// <summary>A stored version failed authentication or does not belong where it was found.</summary>
public sealed class SecretIntegrityException(string message) : Exception(message);

/// <summary>The key-encryption key is missing, malformed, unprotected, or stored next to the data.</summary>
public sealed class SecretKeyUnavailableException(string message) : Exception(message);
