using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

public sealed record BatchCoordinatorLease(
    string LeaseId, BatchGateScope Scope, string Owner, long Fence,
    DateTimeOffset ExpiresAtUtc, DateTimeOffset AcquiredAtUtc = default,
    string? Host = null, int? ProcessId = null, DateTimeOffset? ProcessStartedAtUtc = null);

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
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    public string RecoveryBlocker(BatchGateScope scope)
    {
        FileStream guard;
        try { guard = Guard(scope); }
        catch (IOException) { return "lock busy"; }
        using (guard)
        {
            try
            {
                var holder = Read(scope);
                return holder is { } lease && lease.ExpiresAtUtc > _now()
                    ? $"lease held by {lease.Owner}" : "lease record changed before retry";
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException
                or JsonException) { return "lease record unreadable"; }
        }
    }

    public BatchGateLeaseService(string? root = null, Func<DateTimeOffset>? now = null)
    {
        _root = root ?? LocalStateRoot.Combine("agentstudio", "batch-gate-leases");
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public BatchCoordinatorLease? TryAcquire(BatchGateScope scope, string owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Owner is required.", nameof(owner));
        using var guard = Guard(scope);
        var existing = Read(scope);
        var now = _now();
        if (existing is { } current && current.ExpiresAtUtc > now && !OwnerProcessExited(current))
            return null;
        using var process = Process.GetCurrentProcess();
        var next = new BatchCoordinatorLease(Guid.NewGuid().ToString("N"), scope,
            owner, (existing?.Fence ?? 0) + 1, now + Ttl, now,
            Environment.MachineName, Environment.ProcessId,
            new DateTimeOffset(process.StartTime.ToUniversalTime()));
        Write(scope, next);
        return next;
    }

    public bool IsBusy(BatchGateScope scope)
    {
        try
        {
            using var guard = Guard(scope);
            return Read(scope) is { } current && current.ExpiresAtUtc > _now()
                && !OwnerProcessExited(current);
        }
        catch (IOException) { return true; }
    }

    public BatchCoordinatorLease? Renew(BatchCoordinatorLease lease)
    {
        try
        {
            using var guard = Guard(lease.Scope);
            var current = Read(lease.Scope);
            // One clock read: an expiry check against one instant and an
            // extension from a later one could revive a grant that expired
            // in between.
            var now = _now();
            if (!Matches(current, lease) || current!.ExpiresAtUtc <= now) return null;
            var renewed = current with { ExpiresAtUtc = now + Ttl };
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

    private static bool OwnerProcessExited(BatchCoordinatorLease lease)
    {
        if (lease.Host != Environment.MachineName || lease.ProcessId is not > 0
            || lease.ProcessStartedAtUtc is null) return false;
        try
        {
            using var process = Process.GetProcessById(lease.ProcessId.Value);
            return new DateTimeOffset(process.StartTime.ToUniversalTime()) != lease.ProcessStartedAtUtc.Value;
        }
        catch (ArgumentException) { return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (InvalidOperationException) { return true; }
    }
}
