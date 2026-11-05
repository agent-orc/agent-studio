using System.Text.Json;
using AgentStudio.Security;

namespace AgentStudio.Operator;

/// <summary>Body of <c>POST /api/operator/watcher/lease</c>.</summary>
public sealed record WatcherLeaseRequest(string? Session = null, bool TakeOver = false, string? Reason = null);

/// <summary>The lease as the routes return it.</summary>
public sealed record WatcherLeaseResponse(
    string LeaseId, string Holder, string? Session, DateTime GrantedAtUtc, DateTime LastHeartbeatUtc,
    DateTime ExpiresAtUtc, int HeartbeatSeconds, string? TakenOverFrom);

public sealed record WatcherLeaseResult(WatcherLeaseOutcome Outcome, WatcherLease? Lease, WatcherLease? Previous);

/// <summary>
/// Holds the installation's watcher lease in <c>.operator/watcher-lease.json</c>.
/// A grant, a take-over, a renewal and a release each publish a
/// <c>watcher.heartbeat</c> event. Grants, take-overs and releases are written
/// to the control plane audit; a take-over records the previous holder and its
/// last heartbeat.
/// </summary>
public sealed class WatcherLeaseService
{
    public const string FileName = "watcher-lease.json";
    public const string LeaseHref = "/api/operator/watcher/lease";

    private readonly string? _folder;
    private readonly OperatorDecisionOptions _options;
    private readonly OperatorDecisionLog _log;
    private readonly ControlPlaneAuditLog? _audit;
    private readonly TimeProvider _clock;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private WatcherLease? _lease;
    private bool _loaded;

    public WatcherLeaseService(string? folder, OperatorDecisionOptions options, OperatorDecisionLog log,
        ControlPlaneAuditLog? audit, TimeProvider clock, ILogger? logger = null)
    {
        _folder = folder;
        _options = options;
        _log = log;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    private string? LeasePath => _folder is null ? null : Path.Combine(_folder, FileName);
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>The active lease, or null when nobody holds one.</summary>
    public WatcherLease? Current()
    {
        lock (_gate)
        {
            LoadLocked();
            return WatcherLeasePolicy.IsActive(_lease, Now, _options.WatcherLeaseTimeout) ? _lease : null;
        }
    }

    /// <summary>
    /// Grants the lease, or takes it over when the request carries the
    /// explicit flag. The audit entry is written before the lease changes, so
    /// a take-over the audit could not record does not happen.
    /// </summary>
    public WatcherLeaseResult Acquire(string actor, WatcherLeaseRequest request)
    {
        WatcherLease lease;
        WatcherLease? previous;
        lock (_gate)
        {
            LoadLocked();
            var now = Now;
            var decision = WatcherLeasePolicy.Request(_lease, request.TakeOver, now, _options.WatcherLeaseTimeout);
            if (decision.Outcome == WatcherLeaseOutcome.Conflict)
                return new WatcherLeaseResult(WatcherLeaseOutcome.Conflict, decision.Current, null);
            previous = decision.Outcome == WatcherLeaseOutcome.TakenOver ? decision.Current : null;
            lease = new WatcherLease("wl_" + Guid.NewGuid().ToString("N")[..16], actor,
                Trim(request.Session), now, now, previous?.Actor);
            if (previous is not null)
                Audit(actor, "watcher.lease.take-over", "taken-over", previous.Actor, new
                {
                    leaseId = lease.LeaseId, session = lease.Session, reason = Trim(request.Reason),
                    previousLeaseId = previous.LeaseId, previousHolder = previous.Actor, previousSession = previous.Session,
                    previousLastHeartbeatUtc = previous.LastHeartbeatUtc,
                });
            else
                Audit(actor, "watcher.lease.grant", "granted", null, new { leaseId = lease.LeaseId, session = lease.Session });
            SaveLocked(lease);
        }
        Publish(lease, previous is null
            ? $"Watcher lease granted to {Describe(lease)}."
            : $"Watcher lease taken over by {Describe(lease)} from {previous.Actor} (last heartbeat {previous.LastHeartbeatUtc:O}).");
        return new WatcherLeaseResult(previous is null ? WatcherLeaseOutcome.Granted : WatcherLeaseOutcome.TakenOver,
            lease, previous);
    }

    public WatcherLeaseResult Heartbeat(string leaseId, string actor)
    {
        WatcherLease renewed;
        lock (_gate)
        {
            LoadLocked();
            if (WatcherLeasePolicy.Heartbeat(_lease, leaseId, actor) == WatcherHeartbeatOutcome.Lost)
                return new WatcherLeaseResult(WatcherLeaseOutcome.Conflict, Active(), null);
            renewed = _lease! with { LastHeartbeatUtc = Now };
            SaveLocked(renewed);
        }
        Publish(renewed, $"Watcher {Describe(renewed)} is alive.");
        return new WatcherLeaseResult(WatcherLeaseOutcome.Granted, renewed, null);
    }

    public bool Release(string leaseId, string actor)
    {
        WatcherLease released;
        lock (_gate)
        {
            LoadLocked();
            if (!WatcherLeasePolicy.IsHolder(_lease, leaseId, actor)) return false;
            released = _lease!;
            Audit(actor, "watcher.lease.release", "released", null, new { leaseId = released.LeaseId, session = released.Session });
            SaveLocked(null);
        }
        Publish(released, $"Watcher lease of {Describe(released)} released.");
        return true;
    }

    public WatcherLeaseResponse View(WatcherLease lease) => new(
        lease.LeaseId, lease.Actor, lease.Session, lease.GrantedAtUtc, lease.LastHeartbeatUtc,
        WatcherLeasePolicy.ExpiresAt(lease, _options.WatcherLeaseTimeout),
        (int)Math.Max(1, _options.WatcherLeaseTimeout.TotalSeconds / 3), lease.TakenOverFrom);

    private WatcherLease? Active() => WatcherLeasePolicy.IsActive(_lease, Now, _options.WatcherLeaseTimeout) ? _lease : null;

    private void Publish(WatcherLease lease, string reason)
        => _log.Append([new OperatorDecisionFact(OperatorDecisionTypes.WatcherHeartbeat,
            $"{OperatorDecisionTypes.WatcherHeartbeat}|{lease.LeaseId}", null, null, reason, Now,
            [new OperatorEvidenceLink("watcher-lease", LeaseHref)])]);

    private void Audit(string actor, string operation, string outcome, string? target, object details)
        => _audit?.Append(new ControlPlaneAuditRecord(ControlPlaneAuditStreams.Operator, actor, operation, outcome,
            target ?? LeaseHref, Details: details));

    private void LoadLocked()
    {
        if (_loaded) return;
        _loaded = true;
        if (LeasePath is not { } path || !File.Exists(path)) return;
        try { _lease = JsonSerializer.Deserialize<WatcherLease>(File.ReadAllText(path), OperatorDecisionLog.Json); }
        catch (JsonException exception)
        {
            _logger?.LogWarning(exception, "watcher lease file unreadable; treated as no lease");
        }
    }

    private void SaveLocked(WatcherLease? lease)
    {
        _lease = lease;
        if (LeasePath is not { } path) return;
        if (lease is null)
        {
            File.Delete(path);
            return;
        }
        Directory.CreateDirectory(_folder!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(lease, OperatorDecisionLog.Json));
        File.Move(temp, path, overwrite: true);
    }

    private static string Describe(WatcherLease lease)
        => lease.Session is { Length: > 0 } session ? $"{lease.Actor} ({session})" : lease.Actor;

    private static string? Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[..200];
    }
}
