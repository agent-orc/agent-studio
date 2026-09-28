using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Pipeline;

public sealed record BatchCoordinatorLease(
    string LeaseId, BatchGateScope Scope, string Owner, long Fence,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Persistent coordinator lease scoped to project, repository, branch and
/// profile. A cross-process file lock serializes fence advancement. Every
/// resume and publish path must recheck the persisted lease immediately before
/// acting; a superseded or expired grant has no authority.
/// </summary>
public sealed class BatchGateLeaseService
{
    private readonly string _root;
    private readonly Func<DateTimeOffset> _now;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    public BatchGateLeaseService(string? root = null, Func<DateTimeOffset>? now = null)
    {
        var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _root = root ?? Path.Combine(data, "agentstudio", "batch-gate-leases");
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public BatchCoordinatorLease? TryAcquire(BatchGateScope scope, string owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Owner is required.", nameof(owner));
        using var guard = Guard(scope);
        var existing = Read(scope);
        var now = _now();
        if (existing is { } current && current.ExpiresAtUtc > now)
            return null;
        var next = new BatchCoordinatorLease(Guid.NewGuid().ToString("N"), scope,
            owner, (existing?.Fence ?? 0) + 1, now + Ttl);
        Write(scope, next);
        return next;
    }

    public BatchCoordinatorLease? Renew(BatchCoordinatorLease lease)
    {
        try
        {
            using var guard = Guard(lease.Scope);
            var current = Read(lease.Scope);
            if (!Matches(current, lease) || current!.ExpiresAtUtc <= _now()) return null;
            var renewed = current with { ExpiresAtUtc = _now() + Ttl };
            Write(lease.Scope, renewed);
            return renewed;
        }
        catch (IOException) { return null; }
    }

    public bool IsCurrent(BatchCoordinatorLease lease)
    {
        try
        {
            using var guard = Guard(lease.Scope);
            var current = Read(lease.Scope);
            return Matches(current, lease) && current!.ExpiresAtUtc > _now();
        }
        catch (IOException) { return false; }
    }

    public bool Release(BatchCoordinatorLease lease)
    {
        try
        {
            using var guard = Guard(lease.Scope);
            var current = Read(lease.Scope);
            if (!Matches(current, lease)) return false;
            Write(lease.Scope, current! with { ExpiresAtUtc = _now() });
            return true;
        }
        catch (IOException) { return false; }
    }

    private FileStream Guard(BatchGateScope scope)
    {
        var folder = Folder(scope);
        Directory.CreateDirectory(folder);
        // Contention is a failed lease acquisition. Callers retry with backoff.
        return new FileStream(Path.Combine(folder, "lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    private BatchCoordinatorLease? Read(BatchGateScope scope)
    {
        var path = Path.Combine(Folder(scope), "lease.json");
        if (!File.Exists(path)) return null;
        var lease = JsonSerializer.Deserialize<BatchCoordinatorLease>(File.ReadAllBytes(path));
        if (lease is null || lease.Scope != scope)
            throw new InvalidDataException("Coordinator lease scope is corrupt.");
        return lease;
    }

    private void Write(BatchGateScope scope, BatchCoordinatorLease lease)
    {
        var path = Path.Combine(Folder(scope), "lease.json");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, lease);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    private string Folder(BatchGateScope scope)
    {
        var key = JsonSerializer.Serialize(new[]
        {
            scope.Project, scope.Repository, scope.IntegrationBranch,
            scope.GateProfile, scope.GateProfileDigest, scope.PlatformVersion,
        });
        return Path.Combine(_root, Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(key))));
    }

    private static bool Matches(BatchCoordinatorLease? current, BatchCoordinatorLease provided)
        => current is not null && current.LeaseId == provided.LeaseId
           && current.Fence == provided.Fence && current.Owner == provided.Owner
           && current.Scope == provided.Scope;
}
