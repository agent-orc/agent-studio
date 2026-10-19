using System.Text;

namespace AgentStudio.Secrets;

/// <summary>
/// Default <see cref="ISecretProvider"/>: validates input, applies the pure
/// version and access policies, persists through the configured
/// <see cref="ISecretStore"/>, and audits every attempt with its outcome. The
/// only path that decrypts a value is <see cref="GetForWorkloadAsync"/>.
/// </summary>
public sealed class SecretProvider : ISecretProvider
{
    public const int MaximumValueBytes = 64 * 1024;

    private readonly ISecretStore _store;
    private readonly SecretAuditLog _audit;
    private readonly SecretValueScrubber _scrubber;
    private readonly TimeProvider _clock;
    private readonly ILogger<SecretProvider> _logger;

    public SecretProvider(ISecretStore store, SecretAuditLog audit, SecretValueScrubber scrubber, TimeProvider clock,
        ILogger<SecretProvider> logger)
    {
        _store = store;
        _audit = audit;
        _scrubber = scrubber;
        _clock = clock;
        _logger = logger;
    }

    public string StoreId => _store.StoreId;
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public Task<SecretRecord> PutAsync(SecretScope scope, string name, string value, SecretActor actor, CancellationToken ct = default)
        => WriteAsync(SecretAuditActions.Put, scope, name, value, actor,
            current => SecretVersionPolicy.Put(current, current?.Id ?? string.Empty, scope, name, actor, Now), ct);

    public Task<SecretRecord> RotateAsync(SecretScope scope, string name, string value, SecretActor actor, CancellationToken ct = default)
        => WriteAsync(SecretAuditActions.Rotate, scope, name, value, actor,
            current => SecretVersionPolicy.Rotate(current, new SecretReference(scope, name), actor, Now), ct);

    public async Task<SecretRecord> RevokeAsync(SecretScope scope, string name, int? version, SecretActor actor, CancellationToken ct = default)
    {
        var reference = new SecretReference(scope, SecretReference.ValidateName(name), version);
        RequireHuman(actor, SecretAuditActions.Revoke);
        return await AuditedAsync(SecretAuditActions.Revoke, reference, actor, null, null, async () =>
        {
            var before = await _store.FindAsync(scope, name, ct) ?? throw new SecretNotFoundException(reference.ToString());
            var after = await _store.UpdateAsync(scope, name, current => SecretVersionPolicy.Revoke(current, version, actor, Now), ct);
            return (after, ReferenceEquals(before, after) || before.Versions.SequenceEqual(after.Versions) ? "already-revoked" : "ok");
        });
    }

    public async Task DeleteAsync(SecretScope scope, string name, SecretActor actor, CancellationToken ct = default)
    {
        var reference = new SecretReference(scope, SecretReference.ValidateName(name));
        RequireHuman(actor, SecretAuditActions.Delete);
        await AuditedAsync(SecretAuditActions.Delete, reference, actor, null, null, async () =>
        {
            if (!await _store.DeleteAsync(scope, name, ct)) throw new SecretNotFoundException(reference.ToString());
            return (true, "ok");
        });
    }

    public async Task<IReadOnlyList<SecretRecord>> ListAsync(SecretScope? scope = null, CancellationToken ct = default)
    {
        var records = await _store.ListAsync(ct);
        return scope is null ? records : records.Where(record => record.ParsedScope == scope).ToList();
    }

    public Task<IReadOnlyList<SecretAuditEntry>> ReadAuditAsync(SecretScope? scope, int limit, CancellationToken ct = default)
        => Task.FromResult(_audit.Read(scope, limit));

    public async Task<ResolvedSecret> GetForWorkloadAsync(SecretReference reference, SecretWorkload workload, string purpose, CancellationToken ct = default)
    {
        var decision = SecretAccessPolicy.ForWorkload(reference, workload, purpose);
        if (!decision.Allowed)
        {
            // Only a validated purpose enters the audit; caller text is not trusted.
            Audit(SecretAuditActions.Resolve, reference, workload.Actor,
                SecretAccessPolicy.IsValidPurpose(purpose) ? purpose : null, workload, "denied:" + decision.Code);
            throw new SecretAccessDeniedException(decision.Code, decision.Reason);
        }

        return await AuditedAsync(SecretAuditActions.Resolve, reference, workload.Actor, purpose, workload, async () =>
        {
            SecretRecord? record = null;
            foreach (var candidate in reference.Scope.InheritanceChain())
            {
                record = await _store.FindAsync(candidate, reference.Name, ct);
                if (record is not null) break;
            }
            if (record is null) throw new SecretNotFoundException(reference.ToString());

            var version = SecretVersionPolicy.Select(record, reference.Version);
            var bytes = await _store.ReadValueAsync(record.ParsedScope, record.Name, version.Version, ct);
            string value;
            try { value = Encoding.UTF8.GetString(bytes); }
            finally { Array.Clear(bytes); }
            await _store.UpdateAsync(record.ParsedScope, record.Name,
                current => SecretVersionPolicy.MarkUsed(current, version.Version, workload, purpose, Now), ct);
            var resolved = new SecretReference(record.ParsedScope, record.Name, version.Version);
            _scrubber.Register(resolved, value);
            return (new ResolvedSecret(reference, resolved, value), "ok", version.Version);
        });
    }

    private async Task<SecretRecord> WriteAsync(string action, SecretScope scope, string name, string value, SecretActor actor,
        Func<SecretRecord?, SecretRecord> transition, CancellationToken ct)
    {
        var reference = new SecretReference(scope, SecretReference.ValidateName(name));
        RequireHuman(actor, action);
        var bytes = ValidateValue(value);
        try
        {
            return await AuditedAsync(action, reference, actor, null, null, async () =>
            {
                var record = await _store.AppendVersionAsync(scope, name, bytes, transition, ct);
                return (record, "ok", (int?)record.Versions.Max(item => item.Version));
            });
        }
        finally { Array.Clear(bytes); }
    }

    private static byte[] ValidateValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            throw new SecretValidationException("value-required", "A secret value is required.");
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaximumValueBytes)
        {
            Array.Clear(bytes);
            throw new SecretValidationException("value-too-large", $"A secret value may hold at most {MaximumValueBytes} bytes.");
        }
        return bytes;
    }

    /// <summary>Workloads only resolve. Every change to a secret is attributed to a human.</summary>
    private static void RequireHuman(SecretActor actor, string action)
    {
        if (actor.Kind != SecretActorKind.Human || string.IsNullOrWhiteSpace(actor.Id))
            throw new SecretAccessDeniedException("human-required", $"Only a signed-in person may {action} a secret.");
    }

    private Task<T> AuditedAsync<T>(string action, SecretReference reference, SecretActor actor, string? purpose,
        SecretWorkload? workload, Func<Task<(T Result, string Outcome)>> operation)
        => AuditedAsync(action, reference, actor, purpose, workload, async () =>
        {
            var (result, outcome) = await operation();
            return (result, outcome, reference.Version);
        });

    private async Task<T> AuditedAsync<T>(string action, SecretReference reference, SecretActor actor, string? purpose,
        SecretWorkload? workload, Func<Task<(T Result, string Outcome, int? Version)>> operation)
    {
        try
        {
            var (result, outcome, version) = await operation();
            Audit(action, reference, actor, purpose, workload, outcome, version);
            return result;
        }
        catch (SecretValidationException ex)
        {
            Audit(action, reference, actor, purpose, workload, "failed:" + ex.Code);
            throw;
        }
        catch (SecretIntegrityException ex)
        {
            _logger.LogError("Secret {Reference} failed an integrity check during {Action}: {Reason}", reference.Unpinned(), action, ex.Message);
            Audit(action, reference, actor, purpose, workload, "failed:integrity");
            throw;
        }
        catch (SecretKeyUnavailableException ex)
        {
            _logger.LogError("Secret {Action} for {Reference} has no usable key-encryption key: {Reason}", action, reference.Unpinned(), ex.Message);
            Audit(action, reference, actor, purpose, workload, "failed:key-unavailable");
            throw;
        }
    }

    private void Audit(string action, SecretReference reference, SecretActor actor, string? purpose, SecretWorkload? workload,
        string outcome, int? version = null)
        => _audit.Append(new SecretAuditEntry
        {
            At = Now,
            Action = action,
            Actor = actor.ToString(),
            Scope = reference.Scope.Key,
            Reference = reference.Unpinned().ToString(),
            Version = version ?? reference.Version,
            Purpose = purpose,
            Workload = workload is null ? null : $"{workload.Kind}:{workload.Project}{(workload.Environment is null ? "" : "/" + workload.Environment)}",
            Outcome = outcome
        });
}
