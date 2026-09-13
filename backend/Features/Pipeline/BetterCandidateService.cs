using System.Collections.Concurrent;
using System.Globalization;
using TokenEconomy;

namespace AgentStudio.Pipeline;

/// <summary>
/// Cached boundary around TokenEconomy's benchmark candidate query. Results
/// are informational and never mutate a selected route.
/// </summary>
public sealed class BetterCandidateService
{
    public const string DefaultCapabilityClass = "CodingAgent";
    public const string MatrixUrl = "https://agent-orchestrator.dev/token-economy/model-benchmarks/";

    private static readonly BenchmarkTokenAssumption DefaultAssumption = new(
        InputTokensPerTask: 100_000,
        OutputTokensPerTask: 10_000,
        CacheReadTokensPerTask: 0,
        CacheWriteTokensPerTask: 0);

    private readonly ModelBenchmarkMatrix _matrix;
    private readonly BenchmarkEvidenceCatalog _evidence;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<QueryCacheKey, IReadOnlyList<CachedCandidate>> _cache = new();

    internal int CachedQueryCount => _cache.Count;

    public BetterCandidateService()
        : this(ModelBenchmarkMatrix.Default, BenchmarkEvidenceCatalog.Default, TimeProvider.System)
    {
    }

    internal BetterCandidateService(
        ModelBenchmarkMatrix matrix,
        BenchmarkEvidenceCatalog evidence,
        TimeProvider timeProvider)
    {
        _matrix = matrix;
        _evidence = evidence;
        _timeProvider = timeProvider;
    }

    public TaskInfo Attach(TaskInfo task, ProjectSettings settings)
    {
        if (!string.Equals(task.State, TaskStates.Ready, StringComparison.OrdinalIgnoreCase))
            return task with { BetterCandidates = null };

        return task with
        {
            BetterCandidates = FindRoute(
                task.Model,
                task.ThinkingLevel,
                settings.BenchmarkCapabilityClass),
        };
    }

    public BetterCandidateNote? FindRoute(
        string? model,
        string? thinkingLevel,
        string? capabilityClass)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (!TryCapabilityClass(capabilityClass, out var capability)) return null;
        if (!TryEffort(thinkingLevel, out var effort)) return null;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var benchmarkTypes = _evidence.Types
            .Where(type => type.CapabilityClass == capability)
            .OrderBy(type => type.Id, StringComparer.Ordinal)
            .ToList();
        if (benchmarkTypes.Count == 0) return null;

        var projected = new List<BetterCandidate>();
        var snapshots = new List<string>();
        foreach (var benchmark in benchmarkTypes)
        {
            var snapshot = EvidenceSnapshot(benchmark);
            snapshots.Add(snapshot);
            var key = new QueryCacheKey(model.Trim(), effort, benchmark.Id, snapshot);
            var candidates = _cache.GetOrAdd(key, _ => Query(model.Trim(), effort, benchmark, snapshot));
            foreach (var candidate in candidates)
            {
                var age = Math.Max(0, DateOnly.FromDateTime(now).DayNumber - candidate.EvidenceDate.DayNumber);
                projected.Add(new BetterCandidate
                {
                    Model = candidate.Model,
                    ThinkingLevel = candidate.ThinkingLevel,
                    BenchmarkType = benchmark.Id,
                    BenchmarkName = benchmark.Name,
                    ScoreDelta = candidate.ScoreDelta,
                    CostDeltaUsd = candidate.CostDeltaUsd,
                    EvidenceAgeDays = age,
                    EvidenceStale = age > 90,
                });
            }
        }

        if (projected.Count == 0) return null;
        return new BetterCandidateNote
        {
            CurrentModel = model.Trim(),
            CurrentThinkingLevel = NormalizeEffort(effort),
            CapabilityClass = capability.ToString(),
            EvidenceSnapshot = string.Join('|', snapshots),
            EvaluatedAtUtc = now,
            MatrixUrl = MatrixUrl,
            Candidates = projected
                .OrderBy(candidate => candidate.CostDeltaUsd ?? decimal.MaxValue)
                .ThenByDescending(candidate => candidate.ScoreDelta ?? decimal.MinValue)
                .ThenBy(candidate => candidate.Model, StringComparer.Ordinal)
                .ToList(),
        };
    }

    public static string Summary(BetterCandidateNote note)
        => string.Join("; ", note.Candidates.Select(candidate =>
            $"{candidate.Model}/{candidate.ThinkingLevel ?? "model default"} on {candidate.BenchmarkName}: " +
            $"score {Signed(candidate.ScoreDelta)}, cost {SignedUsd(candidate.CostDeltaUsd)}, " +
            $"evidence {candidate.EvidenceAgeDays.ToString(CultureInfo.InvariantCulture)}d old"));

    private IReadOnlyList<CachedCandidate> Query(
        string model,
        EffortLevel effort,
        BenchmarkType benchmark,
        string snapshot)
    {
        _ = snapshot;
        try
        {
            var asOf = benchmark.RetrievedAt.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var current = new BenchmarkCellKey(ModelId.Of(model), effort);
            return _matrix.FindCandidates(current, benchmark.Id, DefaultAssumption, asOf)
                .Select(candidate =>
                {
                    var evidenceDate = candidate.Evidence.Count > 0
                        ? candidate.Evidence.Max(item => item.Result.RetrievedAt)
                        : benchmark.RetrievedAt;
                    return new CachedCandidate(
                        candidate.Cell.Key.ModelId.ToString(),
                        NormalizeEffort(candidate.Cell.Key.Effort),
                        candidate.Cell.ScoreDeltaToReference,
                        candidate.Cell.CostDeltaToReferenceUsd,
                        evidenceDate);
                })
                .ToList();
        }
        catch (ArgumentException)
        {
            // The selected route has no comparable cell in this benchmark.
            return [];
        }
    }

    private string EvidenceSnapshot(BenchmarkType benchmark)
    {
        var results = _evidence.ResultsFor(benchmark.Id);
        var latest = results.Count == 0
            ? benchmark.RetrievedAt
            : results.Max(result => result.RetrievedAt);
        return string.Join(':',
            _evidence.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            benchmark.Id,
            benchmark.Version,
            latest.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            results.Count.ToString(CultureInfo.InvariantCulture));
    }

    private static bool TryCapabilityClass(string? value, out BenchmarkCapabilityClass capability)
        => Enum.TryParse(
            string.IsNullOrWhiteSpace(value) ? DefaultCapabilityClass : value.Trim(),
            ignoreCase: true,
            out capability);

    private static bool TryEffort(string? value, out EffortLevel effort)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            effort = EffortLevel.Unspecified;
            return true;
        }
        return Enum.TryParse(value.Replace("-", string.Empty), ignoreCase: true, out effort);
    }

    private static string? NormalizeEffort(EffortLevel effort) => effort switch
    {
        EffortLevel.Unspecified => null,
        EffortLevel.XHigh => "xhigh",
        _ => effort.ToString().ToLowerInvariant(),
    };

    private static string Signed(decimal? value)
        => value.HasValue ? value.Value.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) : "n/a";

    private static string SignedUsd(decimal? value)
        => value.HasValue ? value.Value.ToString("+$0.####;-$0.####;$0", CultureInfo.InvariantCulture) : "n/a";

    private sealed record QueryCacheKey(string Model, EffortLevel Effort, string BenchmarkType, string EvidenceSnapshot);
    private sealed record CachedCandidate(
        string Model,
        string? ThinkingLevel,
        decimal? ScoreDelta,
        decimal? CostDeltaUsd,
        DateOnly EvidenceDate);
}
