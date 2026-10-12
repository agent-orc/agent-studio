using System.Collections.Concurrent;
using System.Diagnostics;

namespace AgentStudio.Tasks;

/// <summary>
/// Loads the status.md versions a card committed to the workspace repository
/// before <see cref="ResultVersionStore"/> existed, off the request path
/// (AGT-3086). The walk over the whole workspace repository took 7-21 s per
/// card, so it runs in the background and its answer is persisted per card in
/// <see cref="LegacyResultHistoryFile"/>.
///
/// <para>There is no sweep and no hosted loop. <see cref="Observe"/>, called by
/// the Result-history list, starts a backfill for a card that has no persisted
/// outcome or whose failed outcome is due for a retry. Runs are single-flight
/// per card, and one process-wide gate keeps at most one walk on the workspace
/// repository at a time. <see cref="RunBackfillAsync"/> is the one awaitable
/// entry both the scheduler and tests use.</para>
/// </summary>
public sealed class LegacyResultHistoryBackfill
{
    private readonly TaskScannerService _scanner;
    private readonly TaskFileHistoryService _files;
    private readonly TimeProvider _clock;
    private readonly ILogger<LegacyResultHistoryBackfill> _logger;
    private readonly TimeSpan _totalBudget;
    private readonly TimeSpan _perCallCap;

    private readonly ConcurrentDictionary<string, Lazy<Task>> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAttempt = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _repositoryGate = new(1, 1);
    private int _startedRuns;

    public LegacyResultHistoryBackfill(
        TaskScannerService scanner,
        TaskFileHistoryService files,
        TimeProvider clock,
        ILogger<LegacyResultHistoryBackfill> logger,
        TimeSpan? totalBudget = null)
    {
        _scanner = scanner;
        _files = files;
        _clock = clock;
        _logger = logger;
        _totalBudget = totalBudget ?? GitCallBudgetPolicy.BackfillTotalBudget;
        _perCallCap = GitCallBudgetPolicy.BackfillPerCallCap;
    }

    /// <summary>Backfills started by this instance; a test probe for single-flight.</summary>
    internal int StartedRuns => Volatile.Read(ref _startedRuns);

    /// <summary>Completes once every backfill in flight has persisted its outcome.</summary>
    internal Task WhenIdleAsync() => Task.WhenAll(_inFlight.Values.Select(run => run.Value));

    /// <summary>
    /// Reads the card's persisted legacy history, starts a backfill when the
    /// policy says one is due, and returns the header state. Never waits for
    /// git.
    /// </summary>
    public (string State, LegacyResultHistorySnapshot Snapshot) Observe(TaskInfo task, string jobId, string? watchPath)
    {
        var key = Key(task);
        // In-memory facts first, then the file: a run that finishes in between
        // is then seen as complete rather than as "attempted but missing".
        var running = _inFlight.ContainsKey(key);
        DateTimeOffset? lastAttempt = _lastAttempt.TryGetValue(key, out var at) ? at : null;
        var snapshot = LegacyResultHistoryFile.Read(task.FolderPath);

        var decision = LegacyResultHistoryPolicy.Decide(
            snapshot.State,
            snapshot.Record?.ComputedAt,
            lastAttempt,
            running,
            _clock.GetUtcNow());
        if (decision.Schedule) _ = StartOrJoin(key, jobId, watchPath);
        return (decision.State, snapshot);
    }

    /// <summary>
    /// Runs the backfill for one card now, or joins the run already in flight.
    /// Completes once the outcome is persisted.
    /// </summary>
    public Task RunBackfillAsync(string jobId, string? watchPath)
    {
        var task = _scanner.FindJob(jobId, watchPath);
        return task is null ? Task.CompletedTask : StartOrJoin(Key(task), jobId, watchPath);
    }

    private Task StartOrJoin(string key, string jobId, string? watchPath)
    {
        while (true)
        {
            if (_inFlight.TryGetValue(key, out var existing)) return existing.Value;
            Lazy<Task>? run = null;
            run = new Lazy<Task>(
                () => Task.Run(() => RunGuardedAsync(key, run!, jobId, watchPath)),
                LazyThreadSafetyMode.ExecutionAndPublication);
            if (!_inFlight.TryAdd(key, run)) continue;

            Interlocked.Increment(ref _startedRuns);
            _lastAttempt[key] = _clock.GetUtcNow();
            return run.Value;
        }
    }

    private async Task RunGuardedAsync(string key, Lazy<Task> run, string jobId, string? watchPath)
    {
        try
        {
            await RunAsync(jobId, watchPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A background run has no caller to surface this to. The missing
            // or stale file keeps the header honest, and the attempt timestamp
            // holds the next try back for the retry interval.
            _logger.LogWarning(ex, "result-history legacy backfill crashed key={Key}", key);
        }
        finally
        {
            // Leave the in-flight set before the run's task completes, so a
            // caller that awaited it never still sees it running.
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task>>(key, run));
        }
    }

    private async Task RunAsync(string jobId, string? watchPath)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info is null) return;

        var elapsed = Stopwatch.StartNew();
        TaskFileLookupResult<IReadOnlyList<WorkspaceResultFileVersion>> walk;
        await _repositoryGate.WaitAsync().ConfigureAwait(false);
        try
        {
            walk = _files.WalkLegacyResultHistory(info, new GitCallBudget(_totalBudget, _perCallCap));
        }
        finally
        {
            _repositoryGate.Release();
        }
        elapsed.Stop();

        var record = walk.Success
            ? new LegacyResultHistoryRecord(
                LegacyResultHistoryFile.SchemaVersion,
                _clock.GetUtcNow(),
                LegacyResultHistoryStates.Complete,
                null,
                elapsed.ElapsedMilliseconds,
                ToEntries(walk.Value ?? []))
            : new LegacyResultHistoryRecord(
                LegacyResultHistoryFile.SchemaVersion,
                _clock.GetUtcNow(),
                LegacyResultHistoryStates.Failed,
                walk.Error,
                elapsed.ElapsedMilliseconds,
                []);

        // The card may have changed lanes while git ran.
        var folder = _scanner.FindJob(jobId, watchPath)?.FolderPath ?? info.FolderPath;
        try
        {
            record = LegacyResultHistoryFile.Write(folder, record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex,
                "result-history legacy backfill could not persist its outcome jobId={JobId} outcome={Outcome}",
                jobId, record.Outcome);
            return;
        }

        if (record.Outcome == LegacyResultHistoryStates.Failed)
        {
            _logger.LogWarning(
                "result-history legacy backfill failed jobId={JobId} reason={Reason} elapsedMs={ElapsedMs}",
                jobId, record.Reason, record.ElapsedMs);
        }
        else
        {
            _logger.LogInformation(
                "result-history legacy backfill complete jobId={JobId} versions={Versions} elapsedMs={ElapsedMs}",
                jobId, record.Entries.Count, record.ElapsedMs);
        }
    }

    private static IReadOnlyList<LegacyResultHistoryEntry> ToEntries(IReadOnlyList<WorkspaceResultFileVersion> versions)
    {
        var entries = new List<LegacyResultHistoryEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var version in versions)
        {
            var hash = ResultHistoryContent.Hash(version.Content);
            if (!seen.Add(hash)) continue;
            var producer = ResultHistoryContent.InferGitProducer(version.Content, version.Entry);
            var fields = ResultHistoryContent.ParseFields(version.Content);
            entries.Add(new LegacyResultHistoryEntry(
                Sha: version.Entry.Sha,
                Timestamp: version.Entry.At ?? DateTime.MinValue,
                Producer: producer.Label,
                ProducerKind: producer.Kind,
                Lane: ResultHistoryContent.ReadLane(version.TaskJson) ?? "unknown",
                Result: fields.Result,
                Case: fields.Case,
                ContentHash: hash,
                Markdown: version.Content,
                Truncated: false));
        }
        return entries;
    }

    /// <summary>
    /// One card, however its id or watch path was spelled in the request.
    /// </summary>
    private static string Key(TaskInfo task) => Path.GetFullPath(task.FolderPath);
}
