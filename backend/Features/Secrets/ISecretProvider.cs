namespace AgentStudio.Secrets;

/// <summary>
/// The one secret interface every part of the platform talks to (Dossier
/// integrierte-plattform 7.2). It enforces write-without-read for humans,
/// scope checks for workloads, audit, and log scrubbing, whatever
/// <see cref="ISecretStore"/> holds the values. There is deliberately no
/// operation that returns a value to a human actor.
/// </summary>
public interface ISecretProvider
{
    /// <summary>Stores a new version that replaces the secret: earlier versions are revoked.</summary>
    Task<SecretRecord> PutAsync(SecretScope scope, string name, string value, SecretActor actor, CancellationToken ct = default);

    /// <summary>Stores a new current version; earlier versions stay resolvable until revoked.</summary>
    Task<SecretRecord> RotateAsync(SecretScope scope, string name, string value, SecretActor actor, CancellationToken ct = default);

    /// <summary>Revokes one version, or every active version when <paramref name="version"/> is null.</summary>
    Task<SecretRecord> RevokeAsync(SecretScope scope, string name, int? version, SecretActor actor, CancellationToken ct = default);

    Task DeleteAsync(SecretScope scope, string name, SecretActor actor, CancellationToken ct = default);

    /// <summary>Metadata of every secret, or of one scope. Never values.</summary>
    Task<IReadOnlyList<SecretRecord>> ListAsync(SecretScope? scope = null, CancellationToken ct = default);

    Task<IReadOnlyList<SecretAuditEntry>> ReadAuditAsync(SecretScope? scope, int limit, CancellationToken ct = default);

    /// <summary>
    /// Issues a value to a verified run-time workload for a stated purpose.
    /// The reference must lie in the workload's scope; the call is audited and
    /// the value is registered for log scrubbing before it is returned.
    /// </summary>
    Task<ResolvedSecret> GetForWorkloadAsync(SecretReference reference, SecretWorkload workload, string purpose, CancellationToken ct = default);
}

/// <summary>
/// Storage adapter behind <see cref="ISecretProvider"/>. The built-in store
/// encrypts in the installation; external stores (Vault or OpenBao, cloud
/// secret managers, 1Password) implement the same seam. Metadata transitions
/// are computed by <see cref="SecretVersionPolicy"/> and applied by the store
/// atomically under its own concurrency control.
/// </summary>
public interface ISecretStore
{
    string StoreId { get; }

    Task<IReadOnlyList<SecretRecord>> ListAsync(CancellationToken ct);

    Task<SecretRecord?> FindAsync(SecretScope scope, string name, CancellationToken ct);

    /// <summary>
    /// Persists <paramref name="value"/> as the newest version that
    /// <paramref name="transition"/> appends to the current metadata (null when
    /// the secret is new). The value is durable before the metadata names it.
    /// </summary>
    Task<SecretRecord> AppendVersionAsync(
        SecretScope scope, string name, ReadOnlyMemory<byte> value,
        Func<SecretRecord?, SecretRecord> transition, CancellationToken ct);

    /// <summary>Applies a metadata-only transition such as a revocation or a use stamp.</summary>
    Task<SecretRecord> UpdateAsync(SecretScope scope, string name, Func<SecretRecord, SecretRecord> transition, CancellationToken ct);

    /// <summary>Decrypts one version after verifying its integrity.</summary>
    Task<byte[]> ReadValueAsync(SecretScope scope, string name, int version, CancellationToken ct);

    /// <summary>Removes the metadata and every stored version.</summary>
    Task<bool> DeleteAsync(SecretScope scope, string name, CancellationToken ct);
}
