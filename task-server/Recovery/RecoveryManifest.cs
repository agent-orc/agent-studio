using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.TaskServer.Recovery;

/// <summary>
/// Versioned description of one complete installation recovery set (Dossier AGT-W63 D7 option A, item I07).
/// The manifest sits beside the verified full backup set and never replaces its <c>inventory.json</c>
/// and <c>complete.json</c>: the set remains the evidence, the manifest says what else recovery needs.
/// It records references and digests only; no secret value is ever written here.
/// </summary>
public sealed record RecoveryManifest(
    string Schema,
    string ManifestId,
    DateTime CapturedAt,
    string? InstallationId,
    RecoveryStore Store,
    RecoveryIdentities Identities,
    RecoveryDataSet DataSet,
    RecoveryColdEvidence ColdEvidence,
    IReadOnlyList<RecoveryExternalArtifact> ExternalArtifacts,
    IReadOnlyList<RecoveryRepository> Repositories,
    IReadOnlyList<RecoveryConfigurationReference> Configuration,
    RecoverySecretCustody SecretCustody,
    IReadOnlyList<RecoveryHostObligation> PendingHostObligations,
    IReadOnlyList<RecoveryRebuildableCache> RebuildableCaches)
{
    public const string CurrentSchema = "agent-studio.recovery-manifest/v1";
}

/// <summary>Which store is authoritative and the exact release and schema that wrote the set.</summary>
public sealed record RecoveryStore(string Type, string Release, string GitSha, int SchemaVersion);

public static class RecoveryStoreTypes
{
    /// <summary>Standalone Task Server SQLite authority; the set is a database snapshot plus cold payloads.</summary>
    public const string TaskServerSqlite = "task-server-sqlite";

    /// <summary>Legacy file-tree workspace; the set is a Git bundle plus untracked and cold evidence.</summary>
    public const string FileTreeWorkspace = "file-tree-workspace";
}

public sealed record RecoveryIdentities(
    string ServerId,
    IReadOnlyList<RecoveryWorkspaceIdentity> Workspaces,
    IReadOnlyList<RecoveryProjectIdentity> Projects,
    int TaskCount,
    string TaskIdentitySha256);

public sealed record RecoveryWorkspaceIdentity(string WorkspaceId, string Name);

public sealed record RecoveryProjectIdentity(string ProjectId, string WorkspaceId, string Name, string TaskKeyPrefix, int TaskCount);

/// <summary>The set this manifest covers. <c>CompleteMarker</c> must exist before the set may be restored.</summary>
public sealed record RecoveryDataSet(
    string Kind,
    string BackupId,
    string SetSha256,
    string CompleteMarker,
    string Inventory,
    int FileCount,
    long TotalBytes);

/// <summary>Referenced cold payloads carried inside the set under <c>cold/</c>, with their recorded digests.</summary>
public sealed record RecoveryColdEvidence(int PayloadCount, IReadOnlyList<RecoveryColdPayload> Payloads);

public sealed record RecoveryColdPayload(string TaskId, string RelativePath, string Sha256);

/// <summary>Evidence the set does not carry: pointer-only artifacts stored outside the authority.</summary>
public sealed record RecoveryExternalArtifact(string ArtifactId, string RunId, string Name, string Location, string Sha256);

/// <summary>Repository origin and the canonical refs sampled to verify it independently after restore.</summary>
public sealed record RecoveryRepository(string RepositoryId, string? Origin, IReadOnlyList<RecoveryRef> SampledRefs);

public sealed record RecoveryRef(string Name, string Sha, string Source);

/// <summary>Installation configuration the set does not contain, by location and owner only.</summary>
public sealed record RecoveryConfigurationReference(string Name, string Location, string Custody, string? Sha256);

/// <summary>
/// Where encrypted recoverable secrets live and how every active client credential comes back. Principal
/// hashes in the store cannot reconstruct cleartext credentials, so each active principal names a custody.
/// </summary>
public sealed record RecoverySecretCustody(
    string? BundleLocation,
    string? BundleSha256,
    string? Encryption,
    string? RecoveryCredentialHolder,
    IReadOnlyList<RecoveryClientCredential> Clients);

public sealed record RecoveryClientCredential(string PrincipalId, string Kind, string? RunnerId, string Custody);

public static class RecoveryCredentialCustody
{
    /// <summary>The cleartext credential is inside the separately encrypted secret bundle.</summary>
    public const string SecretBundle = "secret-bundle";

    /// <summary>The credential is rotated after restore and redelivered to its client deliberately.</summary>
    public const string ReEnrol = "re-enrol";

    /// <summary>No recovery path was declared; the client cannot reconnect without a rotation.</summary>
    public const string Undeclared = "undeclared";
}

/// <summary>Unreconciled host work: an outbox backlog or a result that exists only as a salvage bundle.</summary>
public sealed record RecoveryHostObligation(
    string Kind,
    string RunnerId,
    string RunId,
    string State,
    long Backlog,
    string Detail);

public static class RecoveryObligationKinds
{
    public const string RunnerOutbox = "runner-outbox";
    public const string SalvageBundle = "salvage-bundle";
}

/// <summary>State the drill does not restore because it is derived and rebuilt on demand.</summary>
public sealed record RecoveryRebuildableCache(string Name, string Location, string RebuiltBy);

/// <summary>Installation-owned custody declaration supplied by the administrator at capture time.</summary>
public sealed record RecoveryCustodyDeclaration(
    string? InstallationId = null,
    IReadOnlyList<RecoveryConfigurationReference>? Configuration = null,
    RecoverySecretBundleDeclaration? SecretBundle = null,
    IReadOnlyList<RecoveryClientCustodyDeclaration>? Clients = null,
    IReadOnlyList<RecoveryRepositoryDeclaration>? Repositories = null);

public sealed record RecoverySecretBundleDeclaration(string Location, string? Encryption, string? RecoveryCredentialHolder);

public sealed record RecoveryClientCustodyDeclaration(string PrincipalId, string Custody);

public sealed record RecoveryRepositoryDeclaration(string RepositoryId, string Origin, IReadOnlyList<string>? Refs = null);

public static class RecoveryJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };
}
