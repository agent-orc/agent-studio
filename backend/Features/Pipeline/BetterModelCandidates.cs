using System.Collections.Concurrent;
using System.Text.Json;
using TokenEconomy;

namespace AgentStudio.Pipeline;

/// <summary>
/// Informational projection of one price-performance candidate returned by
/// TokenEconomy. Agent Studio never applies these routes automatically.
/// </summary>
public sealed record BetterModelCandidate
{
    public string Model { get; init; } = "";
    public string Effort { get; init; } = "";
    public string BenchmarkType { get; init; } = "";
    public string BenchmarkName { get; init; } = "";
    public decimal? ScoreDelta { get; init; }
    public decimal? CostDeltaUsd { get; init; }
    public int EvidenceAgeDays { get; init; }
    public bool EvidenceStale { get; init; }
    public string EvidenceSnapshot { get; init; } = "";
    public string MatrixUrl { get; init; } = BetterModelCandidateService.MatrixUrl;

    public string Note =>
        $"{Model} / {Effort}: {BenchmarkName}, score {FormatDelta(ScoreDelta)}, " +
        $"cost {FormatCostDelta(CostDeltaUsd)}, evidence {EvidenceAgeDays}d old";

    private static string FormatDelta(decimal? value) => value is null ? "n/a" : $"{value:+0.##;-0.##;0}";
    private static string FormatCostDelta(decimal? value) => value is null ? "n/a" : $"{value:+$0.####;-$0.####;$0}";
}

/// <summary>
/// Cached adapter around <see cref="ModelBenchmarkMatrix.FindCandidates"/>.
/// The library recommends; callers only surface the returned evidence.
/// </summary>
public sealed class BetterModelCandidateService
{
    public const string MatrixUrl = "https://agent-orchestrator.dev/token-economy/model-benchmarks/";

    private static readonly BenchmarkTokenAssumption DefaultTokenAssumption =
        new(100_000, 10_000, 0, 0);

    private readonly ModelBenchmarkMatrix _matrix;
    private readonly BenchmarkEvidenceCatalog _evidence;
    private readonly ConcurrentDictionary<CandidateCacheKey, IReadOnlyList<BetterModelCandidate>> _cache = new();

    public BetterModelCandidateService()
        : this(ModelBenchmarkMatrix.Default, BenchmarkEvidenceCatalog.Default)
    {
    }

    internal BetterModelCandidateService(ModelBenchmarkMatrix matrix, BenchmarkEvidenceCatalog evidence)
    {
        _matrix = matrix;
        _evidence = evidence;
    }

    public IReadOnlyList<BetterModelCandidate> Find(
        string? model,
        string? effort,
        string? capabilityClass,
        DateTime? asOfUtc = null)
    {
        if (string.IsNullOrWhiteSpace(model)
            || string.IsNullOrWhiteSpace(effort)
            || !Enum.TryParse<EffortLevel>(effort, true, out var parsedEffort))
            return [];

        var capability = Enum.TryParse<BenchmarkCapabilityClass>(capabilityClass, true, out var parsedCapability)
            ? parsedCapability
            : BenchmarkCapabilityClass.CodingAgent;
        var reference = new BenchmarkCellKey(ModelId.Of(model), parsedEffort);
        var asOf = (asOfUtc ?? DateTime.UtcNow).ToUniversalTime();
        var snapshot = EvidenceSnapshot();

        var benchmarkTypes = _evidence.Types
            .Where(type => type.CapabilityClass == capability)
            .Where(type => _evidence.ResultsFor(type.Id).Any(result =>
                string.Equals(result.ModelId.ToString(), model, StringComparison.OrdinalIgnoreCase)
                && result.ReasoningEffort == parsedEffort))
            .OrderByDescending(type => type.CapturedAt)
            .ThenByDescending(type => type.ValidFrom)
            .ThenBy(type => type.Id, StringComparer.Ordinal)
            .ToList();

        foreach (var benchmarkType in benchmarkTypes)
        {
            var key = new CandidateCacheKey(model.Trim().ToLowerInvariant(), parsedEffort, benchmarkType.Id, snapshot);
            var candidates = _cache.GetOrAdd(key, _ => Query(reference, benchmarkType, asOf, snapshot));
            if (candidates.Count > 0) return candidates;
        }

        return [];
    }

    private IReadOnlyList<BetterModelCandidate> Query(
        BenchmarkCellKey reference,
        BenchmarkType benchmarkType,
        DateTime asOfUtc,
        string snapshot)
        => _matrix.FindCandidates(reference, benchmarkType.Id, DefaultTokenAssumption, asOfUtc)
            .Select(candidate => new BetterModelCandidate
            {
                Model = candidate.Cell.Key.ModelId.ToString(),
                Effort = candidate.Cell.Key.Effort.ToString().ToLowerInvariant(),
                BenchmarkType = benchmarkType.Id,
                BenchmarkName = benchmarkType.Name,
                ScoreDelta = candidate.Cell.ScoreDeltaToReference,
                CostDeltaUsd = candidate.Cell.CostDeltaToReferenceUsd,
                EvidenceAgeDays = candidate.Cell.EvidenceAgeDays,
                EvidenceStale = candidate.Cell.IsStale,
                EvidenceSnapshot = snapshot,
            })
            .ToArray();

    private string EvidenceSnapshot()
    {
        var newest = _evidence.Results.Count == 0
            ? "none"
            : _evidence.Results.Max(result => result.RetrievedAt).ToString("yyyy-MM-dd");
        return $"v{_evidence.SchemaVersion}:{newest}:{_evidence.Results.Count}";
    }

    private readonly record struct CandidateCacheKey(
        string Model,
        EffortLevel Effort,
        string BenchmarkType,
        string EvidenceSnapshot);
}

public sealed record BetterCandidateDecisionSnapshot
{
    public DateTime At { get; init; }
    public string Model { get; init; } = "";
    public string? Effort { get; init; }
    public string Source { get; init; } = "";
    public IReadOnlyList<BetterModelCandidate> Candidates { get; init; } = [];
}

/// <summary>
/// Append-only route evidence used by weekly usage reporting. One record is
/// written before launch, including an empty set when no better route existed.
/// </summary>
public static class BetterCandidateDecisionStore
{
    public const string FileName = "better-candidate-decisions.jsonl";
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Append(string? jobFolder, BetterCandidateDecisionSnapshot decision)
    {
        if (string.IsNullOrWhiteSpace(jobFolder)) return;
        try
        {
            Directory.CreateDirectory(jobFolder);
            var path = Path.Combine(jobFolder, FileName);
            lock (Gates.GetOrAdd(path, _ => new object()))
                File.AppendAllText(path, JsonSerializer.Serialize(decision, JsonOptions) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            // Reporting evidence must never prevent a run from launching.
            SilentCatch.Note(ex, "BetterCandidateDecisionStore.Append: candidate evidence is best-effort");
        }
    }

    public static IReadOnlyList<BetterCandidateDecisionSnapshot> Read(string? jobFolder)
    {
        if (string.IsNullOrWhiteSpace(jobFolder)) return [];
        var path = Path.Combine(jobFolder, FileName);
        if (!File.Exists(path)) return [];
        try
        {
            return File.ReadLines(path)
                .Select(line => JsonSerializer.Deserialize<BetterCandidateDecisionSnapshot>(line, JsonOptions))
                .Where(item => item is not null)
                .Cast<BetterCandidateDecisionSnapshot>()
                .OrderBy(item => item.At)
                .ToArray();
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "BetterCandidateDecisionStore.Read: malformed candidate evidence is ignored");
            return [];
        }
    }
}
