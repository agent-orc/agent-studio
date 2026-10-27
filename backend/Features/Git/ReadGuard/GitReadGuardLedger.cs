using System.Collections.Concurrent;

namespace AgentStudio.Git;

/// <summary>
/// Process-lifetime counters of Git spawns on GET requests, keyed by method and
/// route template. Production never throws (D6), so this ledger and the
/// violation warning are how known debt and new violations stay visible.
/// </summary>
internal sealed class GitReadGuardLedger
{
    private readonly ConcurrentDictionary<string, RouteCounters> _routes = new(StringComparer.Ordinal);

    internal void RecordRequest(string method, string routeTemplate, int spawns, GitReadSpawnVerdict verdict)
    {
        if (spawns == 0) return;
        var counters = For(method, routeTemplate);
        lock (counters)
        {
            counters.Requests++;
            counters.Spawns += spawns;
            counters.MaxSpawns = Math.Max(counters.MaxSpawns, spawns);
            if (verdict == GitReadSpawnVerdict.Unlisted) counters.UnlistedRequests++;
            if (verdict == GitReadSpawnVerdict.CeilingBreached) counters.BreachedRequests++;
        }
    }

    internal void RecordDetached(string method, string routeTemplate)
    {
        var counters = For(method, routeTemplate);
        lock (counters) counters.DetachedSpawns++;
    }

    internal IReadOnlyList<GitReadRouteDebt> Snapshot() => _routes
        .Select(pair =>
        {
            var counters = pair.Value;
            lock (counters)
            {
                var exception = GitReadSpawnExceptionManifest.Find(counters.Method, counters.RouteTemplate);
                return new GitReadRouteDebt(counters.Method, counters.RouteTemplate, exception?.SpawnCeiling,
                    exception?.Owner, exception?.RemovalSlice, counters.Requests, counters.Spawns,
                    counters.MaxSpawns, counters.UnlistedRequests, counters.BreachedRequests,
                    counters.DetachedSpawns);
            }
        })
        .OrderBy(route => route.RouteTemplate, StringComparer.Ordinal)
        .ThenBy(route => route.Method, StringComparer.Ordinal)
        .ToArray();

    private RouteCounters For(string method, string routeTemplate)
        => _routes.GetOrAdd(method + " " + routeTemplate, _ => new RouteCounters(method, routeTemplate));

    private sealed class RouteCounters(string method, string routeTemplate)
    {
        public string Method { get; } = method;
        public string RouteTemplate { get; } = routeTemplate;
        public long Requests;
        public long Spawns;
        public int MaxSpawns;
        public long UnlistedRequests;
        public long BreachedRequests;
        public long DetachedSpawns;
    }
}

/// <summary>
/// One route's Git spawns on read requests since process start. A route with a
/// <see cref="SpawnCeiling"/> is frozen migration debt owned by
/// <see cref="Owner"/> until <see cref="RemovalSlice"/>; one without is a
/// violation.
/// </summary>
public sealed record GitReadRouteDebt(
    string Method,
    string RouteTemplate,
    int? SpawnCeiling,
    string? Owner,
    string? RemovalSlice,
    long RequestsWithSpawns,
    long Spawns,
    int MaxSpawnsPerRequest,
    long UnlistedRequests,
    long BreachedRequests,
    long DetachedSpawns);

/// <summary>The read guard's state for the Admin git-telemetry surface.</summary>
public sealed record GitReadGuardSnapshot(
    GitReadGuardMode Mode,
    string ManifestDigest,
    int FrozenExceptions,
    IReadOnlyList<GitReadRouteDebt> Routes);
