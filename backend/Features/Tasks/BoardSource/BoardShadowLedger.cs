using System.Text.Json;
using AgentStudio.Git;

namespace AgentStudio.Tasks;

public sealed record BoardShadowDifferenceRecord(
    string Fingerprint,
    string Project,
    string TaskKey,
    string Field,
    string? WorkspaceValue,
    string? TaskServerValue,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    long Occurrences,
    bool Present,
    DateTimeOffset? ExplainedAt,
    string? Explanation,
    string? ExplainedBy);

public sealed record BoardShadowSyncStatus(
    long? SyncedGeneration,
    DateTimeOffset? SyncedAt,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    IReadOnlyList<string> MissingProjects,
    int RejectedCards);

/// <summary>
/// Board read acceptance of the card: p95 server side under 200 ms and zero
/// Git spawns per request, over the Git telemetry window of <c>tasks/grouped</c>.
/// </summary>
public sealed record BoardReadAcceptance(
    int SampleCount,
    double? P95Ms,
    double? SpawnsPerRequest,
    double TargetP95Ms,
    double TargetSpawnsPerRequest,
    bool Met)
{
    public const double P95TargetMs = 200;

    public static BoardReadAcceptance From(EndpointStats? stats)
    {
        if (stats is null || stats.SampleCount == 0)
            return new BoardReadAcceptance(0, null, null, P95TargetMs, 0, false);
        var spawns = (double)stats.TotalSpawns / stats.SampleCount;
        return new BoardReadAcceptance(
            stats.SampleCount, stats.P95Ms, spawns, P95TargetMs, 0, stats.P95Ms < P95TargetMs && stats.TotalSpawns == 0);
    }
}

public sealed record BoardSourceStatusResponse(
    string Source,
    string? RejectedSetting,
    bool Mirroring,
    BoardShadowSyncStatus Sync,
    long ComparisonRequests,
    long Comparisons,
    long SkippedComparisons,
    DateTimeOffset? LastComparisonAt,
    long? LastComparedGeneration,
    int UnexplainedDifferences,
    int ExplainedDifferences,
    IReadOnlyList<BoardShadowDifferenceRecord> Differences,
    BoardCutoverReadiness Readiness,
    BoardReadAcceptance? BoardRead = null);

public sealed record ExplainBoardDifferenceRequest(string? Explanation);

/// <summary>
/// Durable record of the shadow comparison: distinct differences by card and
/// field with first and last sighting, operator explanations, and the
/// counters the cutover gate reads. It is persisted beside the prompt
/// telemetry under <c>&lt;TaskRepository&gt;/logs</c>, outside the
/// committed workspace content, so the 7-day window survives a restart
/// without adding workspace commits.
/// </summary>
public sealed class BoardShadowLedger
{
    public const string FileName = "board-shadow-ledger.json";
    internal const int MaxDifferences = 500;
    internal const int ReportedDifferences = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly TimeProvider _clock;
    private readonly ILogger<BoardShadowLedger> _logger;
    private readonly string? _path;
    private readonly Lock _lock = new();
    private LedgerState _state;
    private long _comparisonRequests;

    public BoardShadowLedger(IConfiguration configuration, TimeProvider clock, ILogger<BoardShadowLedger> logger)
        : this(ResolvePath(configuration), clock, logger)
    {
    }

    internal BoardShadowLedger(string? path, TimeProvider clock, ILogger<BoardShadowLedger> logger)
    {
        _clock = clock;
        _logger = logger;
        _path = path;
        _state = Load();
        _comparisonRequests = _state.ComparisonRequests;
    }

    public string? Path => _path;

    /// <summary>Counts a board read that asked for a comparison; lock-free because it runs on the request path.</summary>
    public void RecordComparisonRequest() => Interlocked.Increment(ref _comparisonRequests);

    public void RecordSync(long generation, IReadOnlyCollection<string> missingProjects, int rejectedCards)
    {
        lock (_lock)
        {
            _state.SyncedGeneration = generation;
            _state.SyncedAt = _clock.GetUtcNow();
            _state.MissingProjects = missingProjects.Order(StringComparer.OrdinalIgnoreCase).ToList();
            _state.RejectedCards = rejectedCards;
            SaveLocked();
        }
    }

    public void RecordSyncFailure(string error)
    {
        lock (_lock)
        {
            _state.LastError = error;
            _state.LastErrorAt = _clock.GetUtcNow();
            SaveLocked();
        }
    }

    public void RecordSkippedComparison()
    {
        lock (_lock)
        {
            _state.SkippedComparisons++;
            SaveLocked();
        }
    }

    /// <summary>
    /// Records one comparison of <paramref name="projects"/>. A known
    /// difference that is not reported again is marked absent; one that comes
    /// back with other values loses its explanation, because the explanation
    /// was given for the old values.
    /// </summary>
    public void RecordComparison(
        long generation, IReadOnlyCollection<string> projects, IReadOnlyList<BoardShadowDifference> differences)
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            _state.ContinuousSince = BoardCutoverReadinessPolicy.NextContinuousSince(
                now, _state.ContinuousSince, _state.LastComparisonAt);
            _state.LastComparisonAt = now;
            _state.LastComparedGeneration = generation;
            _state.Comparisons++;
            var compared = new HashSet<string>(projects, StringComparer.OrdinalIgnoreCase);
            var reported = differences.ToDictionary(difference => difference.Fingerprint, StringComparer.Ordinal);
            for (var index = 0; index < _state.Differences.Count; index++)
            {
                var record = _state.Differences[index];
                if (record.Present && compared.Contains(record.Project) && !reported.ContainsKey(record.Fingerprint))
                    _state.Differences[index] = record with { Present = false };
            }
            foreach (var difference in reported.Values)
            {
                var index = _state.Differences.FindIndex(record => record.Fingerprint == difference.Fingerprint);
                if (index < 0)
                {
                    _state.Differences.Add(new BoardShadowDifferenceRecord(
                        difference.Fingerprint, difference.Project, difference.TaskKey, difference.Field,
                        difference.WorkspaceValue, difference.TaskServerValue, now, now, 1, true, null, null, null));
                    continue;
                }
                var known = _state.Differences[index];
                var sameValues = known.WorkspaceValue == difference.WorkspaceValue
                                 && known.TaskServerValue == difference.TaskServerValue;
                _state.Differences[index] = known with
                {
                    WorkspaceValue = difference.WorkspaceValue,
                    TaskServerValue = difference.TaskServerValue,
                    LastSeenAt = now,
                    Occurrences = known.Occurrences + 1,
                    Present = true,
                    ExplainedAt = sameValues ? known.ExplainedAt : null,
                    Explanation = sameValues ? known.Explanation : null,
                    ExplainedBy = sameValues ? known.ExplainedBy : null,
                };
            }
            TrimLocked();
            SaveLocked();
        }
        if (differences.Count > 0)
            _logger.LogWarning(
                "Board shadow comparison at generation {Generation} found {Count} difference(s); first {Fingerprint}",
                generation, differences.Count, differences[0].Fingerprint);
    }

    public bool Explain(string fingerprint, string explanation, string actor)
    {
        if (string.IsNullOrWhiteSpace(explanation)) throw new ArgumentException("An explanation is required.");
        lock (_lock)
        {
            var index = _state.Differences.FindIndex(record => record.Fingerprint == fingerprint);
            if (index < 0) return false;
            _state.Differences[index] = _state.Differences[index] with
            {
                ExplainedAt = _clock.GetUtcNow(),
                Explanation = explanation.Trim(),
                ExplainedBy = actor,
            };
            SaveLocked();
            return true;
        }
    }

    public BoardSourceStatusResponse Snapshot(BoardSourceResolution source)
    {
        lock (_lock)
        {
            var unexplained = _state.Differences.Where(record => record.ExplainedAt is null).ToList();
            var lastUnexplained = unexplained.Count == 0 ? (DateTimeOffset?)null : unexplained.Max(record => record.LastSeenAt);
            return new BoardSourceStatusResponse(
                BoardSourceSetting.Format(source.Mode),
                source.Rejected,
                source.Mode is BoardSourceMode.TaskServerShadow or BoardSourceMode.TaskServer,
                new BoardShadowSyncStatus(
                    _state.SyncedGeneration, _state.SyncedAt, _state.LastError, _state.LastErrorAt,
                    _state.MissingProjects.ToList(), _state.RejectedCards),
                Interlocked.Read(ref _comparisonRequests),
                _state.Comparisons,
                _state.SkippedComparisons,
                _state.LastComparisonAt,
                _state.LastComparedGeneration,
                unexplained.Count,
                _state.Differences.Count - unexplained.Count,
                _state.Differences
                    .OrderBy(record => record.ExplainedAt is null ? 0 : 1)
                    .ThenByDescending(record => record.LastSeenAt)
                    .Take(ReportedDifferences)
                    .ToList(),
                BoardCutoverReadinessPolicy.Evaluate(
                    source.Mode, _clock.GetUtcNow(), _state.ContinuousSince, _state.LastComparisonAt, lastUnexplained));
        }
    }

    /// <summary>Keeps the newest records, dropping explained and absent ones first.</summary>
    private void TrimLocked()
    {
        if (_state.Differences.Count <= MaxDifferences) return;
        _state.Differences = _state.Differences
            .OrderBy(record => record.ExplainedAt is null ? 0 : 1)
            .ThenBy(record => record.Present ? 0 : 1)
            .ThenByDescending(record => record.LastSeenAt)
            .Take(MaxDifferences)
            .ToList();
    }

    private LedgerState Load()
    {
        if (_path is null || !File.Exists(_path)) return new LedgerState();
        try
        {
            return JsonSerializer.Deserialize<LedgerState>(File.ReadAllText(_path), Json) ?? new LedgerState();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(exception, "Board shadow ledger {Path} is unreadable; the comparison window starts again", _path);
            return new LedgerState();
        }
    }

    private void SaveLocked()
    {
        if (_path is null) return;
        _state.ComparisonRequests = Interlocked.Read(ref _comparisonRequests);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_state, Json));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Board shadow ledger {Path} could not be written", _path);
        }
    }

    private static string? ResolvePath(IConfiguration configuration)
    {
        var configured = configuration["Board:Shadow:StatePath"];
        if (!string.IsNullOrWhiteSpace(configured)) return System.IO.Path.GetFullPath(configured);
        var taskRepository = configuration["TaskRepository"];
        if (!string.IsNullOrWhiteSpace(taskRepository)) return System.IO.Path.Combine(taskRepository, "logs", FileName);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) local = System.IO.Path.GetTempPath();
        return System.IO.Path.Combine(local, "agent-taskboard", "logs", FileName);
    }

    private sealed class LedgerState
    {
        public DateTimeOffset? ContinuousSince { get; set; }
        public DateTimeOffset? LastComparisonAt { get; set; }
        public long? LastComparedGeneration { get; set; }
        public long ComparisonRequests { get; set; }
        public long Comparisons { get; set; }
        public long SkippedComparisons { get; set; }
        public long? SyncedGeneration { get; set; }
        public DateTimeOffset? SyncedAt { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset? LastErrorAt { get; set; }
        public List<string> MissingProjects { get; set; } = [];
        public int RejectedCards { get; set; }
        public List<BoardShadowDifferenceRecord> Differences { get; set; } = [];
    }
}
