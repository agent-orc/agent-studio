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
            if (_cache.TryGetValue(key, out var cached)) return ForCaller(cached, allowed);
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
            .Select(fact => new BatchGateMetric(fact.Project, Members(fact), fact,
                Published(fact.BatchId!, fact.BatchRunId!)))
            .ToArray();
        var response = new GateEconomicsResponse(window, now,
            GateEconomicsEndpoints.Rows(projects, batchSamples, since, now, allowed: null),
            BoardRead: PipelineBaselineMetrics.BoardRead(AgentStudio.Git.GitProcessTelemetry.GetStatsSnapshot(
                AgentStudio.Git.GitTelemetryAdminEndpoints.StatsWindow, _time)),
            BoardReadWindowMinutes: AgentStudio.Git.GitTelemetryAdminEndpoints.StatsWindow.TotalMinutes);
        lock (_sync) _cache[key] = response;
        return ForCaller(response, allowed);
    }

    // Cache evidence, then redact for each caller. Equal visible project lists
    // need not imply equal access to an archived or unlisted holding project.
    internal static GateEconomicsResponse ForCaller(GateEconomicsResponse response, Func<string, bool>? allowed)
        => allowed is null ? response : response with
        {
            Rows = response.Rows.Select(row => row.GateHoldingProject is { } holder && !allowed(holder)
                ? row with { GateHoldingProject = GateEconomicsEndpoints.OtherProject } : row).ToArray(),
        };

    private int Members(BatchGateExecutionFact fact)
    {
        if (fact.MemberKeys is not null) return fact.MemberKeys.Distinct(StringComparer.Ordinal).Count();
        // Older facts have no run membership. Count only durable releases for
        // this exact run; manifest membership includes ejected/deferred cards.
        try
        {
            return _batches.ReadManifest(fact.BatchId!).Members.Count(member =>
                _batches.TryReadMember(fact.BatchId!, member.TaskKey, fact.BatchRunId!) is { } record
                && record.BatchRunId == fact.BatchRunId);
        }
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
