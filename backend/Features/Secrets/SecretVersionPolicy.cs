namespace AgentStudio.Secrets;

/// <summary>
/// Pure version lifecycle of a secret. A put replaces the secret (earlier
/// versions are revoked at once); a rotation adds a current version and keeps
/// earlier ones resolvable until each is revoked; resolution picks the pinned
/// version or the newest active one.
/// </summary>
public static class SecretVersionPolicy
{
    public const string ReplacedBy = "replaced:";

    public static SecretRecord Put(SecretRecord? current, string id, SecretScope scope, string name, SecretActor actor, DateTime now)
    {
        if (current is null) return New(id, scope, name, actor, now);
        var next = NextVersion(current);
        var versions = current.Versions
            .Select(version => version.Active
                ? version with { RevokedAt = now, RevokedBy = ReplacedBy + actor }
                : version)
            .Append(NewVersion(next, actor, now, SecretAuditActions.Put))
            .ToList();
        return current with { Versions = versions, UpdatedAt = now, UpdatedBy = actor.ToString() };
    }

    public static SecretRecord Rotate(SecretRecord? current, SecretReference reference, SecretActor actor, DateTime now)
    {
        if (current is null) throw new SecretNotFoundException(reference.ToString());
        var next = NextVersion(current);
        return current with
        {
            Versions = [.. current.Versions, NewVersion(next, actor, now, SecretAuditActions.Rotate)],
            RotatedAt = now,
            RotatedBy = actor.ToString(),
            UpdatedAt = now,
            UpdatedBy = actor.ToString()
        };
    }

    /// <summary>Revokes one version or every active one. Revoking twice is a no-op.</summary>
    public static SecretRecord Revoke(SecretRecord current, int? version, SecretActor actor, DateTime now)
    {
        if (version is { } pinned && current.Versions.All(item => item.Version != pinned))
            throw new SecretNotFoundException(new SecretReference(current.ParsedScope, current.Name, pinned).ToString());
        var changed = false;
        var versions = current.Versions.Select(item =>
        {
            if (!item.Active || (version is { } only && item.Version != only)) return item;
            changed = true;
            return item with { RevokedAt = now, RevokedBy = actor.ToString() };
        }).ToList();
        return changed
            ? current with { Versions = versions, UpdatedAt = now, UpdatedBy = actor.ToString() }
            : current;
    }

    public static SecretVersionRecord Select(SecretRecord record, int? pinned)
    {
        var reference = new SecretReference(record.ParsedScope, record.Name, pinned).ToString();
        if (pinned is { } version)
        {
            var match = record.Versions.FirstOrDefault(item => item.Version == version)
                ?? throw new SecretNotFoundException(reference);
            return match.Active ? match : throw new SecretRevokedException(reference);
        }
        return record.Versions.Where(item => item.Active).MaxBy(item => item.Version)
            ?? throw new SecretRevokedException(reference);
    }

    public static SecretRecord MarkUsed(SecretRecord current, int version, SecretWorkload workload, string purpose, DateTime now)
        => current with
        {
            LastUsedAt = now,
            LastUsedBy = workload.Actor.ToString(),
            LastUsedPurpose = purpose,
            Versions = current.Versions
                .Select(item => item.Version == version ? item with { LastUsedAt = now } : item)
                .ToList()
        };

    /// <summary>The current version a human sees in metadata, or null when every version is revoked.</summary>
    public static int? CurrentVersion(SecretRecord record)
        => record.Versions.Where(item => item.Active).Select(item => (int?)item.Version).Max();

    private static SecretRecord New(string id, SecretScope scope, string name, SecretActor actor, DateTime now) => new()
    {
        Id = id,
        Scope = scope.Key,
        Name = name,
        CreatedAt = now,
        CreatedBy = actor.ToString(),
        UpdatedAt = now,
        UpdatedBy = actor.ToString(),
        Versions = [NewVersion(1, actor, now, SecretAuditActions.Put)]
    };

    private static int NextVersion(SecretRecord current)
        => current.Versions.Count == 0 ? 1 : current.Versions.Max(item => item.Version) + 1;

    private static SecretVersionRecord NewVersion(int version, SecretActor actor, DateTime now, string origin) => new()
    {
        Version = version,
        CreatedAt = now,
        CreatedBy = actor.ToString(),
        Origin = origin
    };
}
