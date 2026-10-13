using System.Text.Json;
using AgentStudio.Registry;
using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

/// <summary>
/// Reads receipts, lane events and batch facts for the gate-economics view.
/// The full scan is cached briefly per window and visible project set, so
/// several open Ops views share one scan instead of one each per poll.
/// </summary>
public sealed class GateEconomicsProjection
{
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private readonly TaskScannerService _scanner;
    private readonly ProjectRegistry _registry;
    private readonly BatchGateStore _batches;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly Dictionary<string, GateEconomicsResponse> _cache = new(StringComparer.Ordinal);

    public GateEconomicsProjection(TaskScannerService scanner, ProjectRegistry registry,
        BatchGateStore batches, TimeProvider time)
    {
        _scanner = scanner;
        _registry = registry;
        _batches = batches;
        _time = time;
    }

    /// <summary>
    /// The rows visible under <paramref name="allowed"/> (null: every project).
    /// Null when a scoped caller is a member of no listed project.
    /// </summary>
    public GateEconomicsResponse? Read(string window, Func<string, bool>? allowed)
    {
        var now = _time.GetUtcNow();
        var jobs = _scanner.ScanAllJobsWithArchive();
        var registered = _registry.List().Where(project => !project.Archived).ToArray();
        var names = registered.Select(project => project.DisplayName)
            .Concat(jobs.Select(job => job.ProjectName))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => allowed is null || allowed(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (allowed is not null && names.Length == 0) return null;

        var key = window + "\n" + string.Join("\n", names);
        lock (_sync)
        {
            foreach (var expired in _cache.Where(entry => now - entry.Value.AsOfUtc >= CacheLifetime)
                         .Select(entry => entry.Key).ToArray())
                _cache.Remove(expired);
            if (_cache.TryGetValue(key, out var cached)) return cached;
        }

        var since = now.AddDays(window == "30d" ? -30 : -7);
        var cache = new GateResultCache();
        var projects = names.Select(name =>
        {
            var record = registered.FirstOrDefault(project => string.Equals(project.DisplayName, name,
                StringComparison.OrdinalIgnoreCase));
            IReadOnlyList<string> folders = jobs.Where(job => string.Equals(job.ProjectName, name,
                StringComparison.OrdinalIgnoreCase)).Select(job => job.FolderPath).ToArray();
            return (name, folders, cache.Report(name), record?.ShortCode, record?.Color);
        }).ToArray();
        var batchSamples = _batches.ListAllExecutions()
            .Where(fact => fact.CompletedAtUtc >= since && fact.CompletedAtUtc <= now
                && fact.BatchId is not null && fact.BatchRunId is not null
                && names.Contains(fact.Project, StringComparer.OrdinalIgnoreCase))
            .Select(fact => new BatchGateMetric(fact.Project, Members(fact.BatchId!), fact,
                Published(fact.BatchId!, fact.BatchRunId!)))
            .ToArray();
        var response = new GateEconomicsResponse(window, now,
            GateEconomicsEndpoints.Rows(projects, batchSamples, since, now, allowed));
        lock (_sync) _cache[key] = response;
        return response;
    }

    private int Members(string batchId)
    {
        try { return _batches.ReadManifest(batchId).Members.Count; }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            SilentCatch.Note(ex, "GateEconomics: batch manifest unavailable");
            return 0;
        }
    }

    private bool Published(string batchId, string batchRunId)
    {
        try
        {
            return string.Equals(_batches.ReadPublication(batchId)?.BatchRunId, batchRunId,
                StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            SilentCatch.Note(ex, "GateEconomics: batch publication unavailable");
            return false;
        }
    }
}
