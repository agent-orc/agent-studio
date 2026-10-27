using System.Collections.Concurrent;
using System.Threading.Channels;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tasks;

/// <summary>
/// Keeps the Task Server board in step with the workspace task index and
/// compares the two (pipeline delivery target, M1 and M2). One worker does
/// both on one serialized cycle: it mirrors the newest published index
/// generation, then, when a board read asked for it, compares that same
/// generation with the Task Server read model. A comparison therefore never
/// races its own sync, and a board read never waits for either.
/// </summary>
public sealed class BoardShadowWorker : BackgroundService
{
    internal const int ImportBatchSize = 500;
    private static readonly TimeSpan MissingProjectRetry = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FailureRetry = TimeSpan.FromSeconds(30);

    private readonly TaskScannerService _scanner;
    private readonly BoardSourceSettings _settings;
    private readonly TaskServerBoardClient _client;
    private readonly TaskServerBoardReader _reader;
    private readonly BoardShadowLedger _ledger;
    private readonly TimeProvider _clock;
    private readonly ILogger<BoardShadowWorker> _logger;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _cycle = new(1, 1);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<string, IReadOnlyDictionary<string, BoardCardCore>> _synced = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _missingRetryAt = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, BoardCardCore>>? _syncedCards;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    private long _syncedGeneration = -1;
    private int _compareRequested;

    public BoardShadowWorker(
        TaskScannerService scanner,
        BoardSourceSettings settings,
        TaskServerBoardClient client,
        TaskServerBoardReader reader,
        BoardShadowLedger ledger,
        TimeProvider clock,
        IConfiguration configuration,
        ILogger<BoardShadowWorker> logger)
    {
        _scanner = scanner;
        _settings = settings;
        _client = client;
        _reader = reader;
        _ledger = ledger;
        _clock = clock;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue("Board:Shadow:SyncIntervalSeconds", 5), 1, 3600));
    }

    /// <summary>The workspace index generation the Task Server last accepted, or -1.</summary>
    public long SyncedGeneration => Interlocked.Read(ref _syncedGeneration);

    /// <summary>Called by a shadow board read: count it and wake the worker. Never blocks.</summary>
    public void RequestComparison()
    {
        _ledger.RecordComparisonRequest();
        Interlocked.Exchange(ref _compareRequested, 1);
        _wake.Writer.TryWrite(true);
    }

    /// <summary>
    /// Runs a cycle on the caller, bounded by <paramref name="timeout"/>, until
    /// the Task Server holds <paramref name="generation"/> or newer. The
    /// task-server board read uses it so a client sees its own write.
    /// </summary>
    public async Task<bool> EnsureSyncedAsync(long generation, TimeSpan timeout, CancellationToken ct)
    {
        if (SyncedGeneration >= generation) return true;
        using var bounded = new CancellationTokenSource(timeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, bounded.Token);
        try
        {
            await RunCycleAsync(linked.Token);
        }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogInformation("Board mirror did not reach generation {Generation} within {Timeout}", generation, timeout);
        }
        return SyncedGeneration >= generation;
    }

    /// <summary>One mirror and compare cycle; serialized, so concurrent callers queue.</summary>
    public async Task RunCycleAsync(CancellationToken ct)
    {
        await _cycle.WaitAsync(ct);
        try
        {
            if (!_settings.MirrorsToTaskServer)
            {
                Reset();
                return;
            }
            var publication = await _scanner.AcquireBoardReadAsync(null, ct);
            if (publication is null) return;
            var now = _clock.GetUtcNow();
            var due = publication.Generation != SyncedGeneration
                      || _missingRetryAt.Values.Any(at => at <= now);
            if (due && now >= _retryAfter) await SyncAsync(publication, ct);
            if (Interlocked.Exchange(ref _compareRequested, 0) == 1
                && _settings.Mode == BoardSourceMode.TaskServerShadow)
                await CompareAsync(publication.Generation, ct);
        }
        finally
        {
            _cycle.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Board shadow cycle failed");
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            await Task.WhenAny(
                _wake.Reader.WaitToReadAsync(wait.Token).AsTask(),
                Task.Delay(_interval, _clock, wait.Token));
            await wait.CancelAsync();
            _wake.Reader.TryRead(out _);
        }
    }

    private void Reset()
    {
        if (SyncedGeneration < 0 && _synced.Count == 0) return;
        _synced.Clear();
        _missingRetryAt.Clear();
        _syncedCards = null;
        _retryAfter = DateTimeOffset.MinValue;
        Interlocked.Exchange(ref _syncedGeneration, -1);
        Interlocked.Exchange(ref _compareRequested, 0);
    }

    private async Task SyncAsync(TaskIndexPublication publication, CancellationToken ct)
    {
        var current = BoardCardCore.Group(publication.Live.Concat(publication.Archive));
        var missing = new List<string>();
        var rejected = 0;
        try
        {
            foreach (var delta in BoardMirrorPlan.Create(_synced, current))
            {
                if (_missingRetryAt.TryGetValue(delta.Project, out var retryAt) && _clock.GetUtcNow() < retryAt)
                {
                    missing.Add(delta.Project);
                    continue;
                }
                var accepted = await ImportAsync(delta, publication.Generation, ct);
                if (accepted is null)
                {
                    missing.Add(delta.Project);
                    _missingRetryAt[delta.Project] = _clock.GetUtcNow() + MissingProjectRetry;
                    continue;
                }
                rejected += accepted.Value;
                _missingRetryAt.TryRemove(delta.Project, out _);
                if (current.TryGetValue(delta.Project, out var cards)) _synced[delta.Project] = cards;
                else _synced.Remove(delta.Project);
            }
        }
        catch (HttpRequestException exception)
        {
            FailSync(exception);
            return;
        }
        catch (TaskCanceledException exception) when (!ct.IsCancellationRequested)
        {
            FailSync(exception);
            return;
        }

        _syncedCards = current;
        Interlocked.Exchange(ref _syncedGeneration, publication.Generation);
        _ledger.RecordSync(publication.Generation, missing, rejected);
        if (rejected > 0)
            _logger.LogWarning("Task Server rejected {Count} workspace card(s) at generation {Generation}",
                rejected, publication.Generation);
    }

    /// <summary>Sends one project's delta in batches. Null when the Task Server does not know the project.</summary>
    private async Task<int?> ImportAsync(BoardMirrorProjectDelta delta, long generation, CancellationToken ct)
    {
        var rejected = 0;
        var batches = delta.Upserts.Chunk(ImportBatchSize).ToList();
        if (batches.Count == 0) batches.Add([]);
        for (var index = 0; index < batches.Count; index++)
        {
            var request = new BoardWorkspaceImportRequest(
                generation,
                batches[index].Select(card => card.ToWorkspaceCard()).ToList(),
                index == batches.Count - 1 ? delta.Removals : []);
            var response = await _client.ImportAsync(delta.Project, request, ct);
            if (response is null) return null;
            rejected += response.Rejected.Count;
        }
        return rejected;
    }

    private void FailSync(Exception exception)
    {
        _retryAfter = _clock.GetUtcNow() + FailureRetry;
        _ledger.RecordSyncFailure(exception.Message);
        _logger.LogWarning(exception, "Board mirror could not reach the Task Server");
    }

    private async Task CompareAsync(long generation, CancellationToken ct)
    {
        var cards = _syncedCards;
        if (cards is null || SyncedGeneration != generation)
        {
            _ledger.RecordSkippedComparison();
            return;
        }
        var differences = new List<BoardShadowDifference>();
        try
        {
            foreach (var (project, projectCards) in cards.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                var model = await _reader.ReadAsync(project, ct);
                differences.AddRange(BoardShadowComparison.Compare(project, projectCards, model));
            }
        }
        catch (HttpRequestException exception)
        {
            _ledger.RecordSkippedComparison();
            _logger.LogWarning(exception, "Board shadow comparison could not read the Task Server");
            return;
        }
        _ledger.RecordComparison(generation, cards.Keys.ToList(), differences);
    }
}

/// <summary>
/// Conditional reads of the Task Server board read model per project. The
/// last model is kept with its validator, so an unchanged board costs a 304.
/// A read is bounded by <see cref="ReadTimeout"/>; after a failed read the
/// reader fails fast for <see cref="OpenFor"/>, so an unreachable Task Server
/// costs a board read one timeout rather than one per poll.
/// </summary>
public sealed class TaskServerBoardReader(TaskServerBoardClient client, TimeProvider clock)
{
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan OpenFor = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, BoardReadModelResponse> _models = new(StringComparer.OrdinalIgnoreCase);
    private long _openUntilTicks;

    /// <summary>Null when the Task Server does not know the project; <see cref="HttpRequestException"/> when it cannot answer.</summary>
    public async Task<BoardReadModelResponse?> ReadAsync(string project, CancellationToken ct)
    {
        if (clock.GetUtcNow().UtcTicks < Interlocked.Read(ref _openUntilTicks))
            throw new HttpRequestException("The Task Server board read failed recently; retrying shortly.");
        using var bounded = new CancellationTokenSource(ReadTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, bounded.Token);
        try
        {
            return await ReadOnceAsync(project, linked.Token);
        }
        catch (HttpRequestException)
        {
            Open();
            throw;
        }
        catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
        {
            Open();
            throw new HttpRequestException($"The Task Server board read for {project} timed out.", exception);
        }
    }

    /// <summary>The last model read for <paramref name="project"/>, for a degraded answer.</summary>
    public BoardReadModelResponse? Cached(string project) => _models.GetValueOrDefault(project);

    private void Open() => Interlocked.Exchange(ref _openUntilTicks, (clock.GetUtcNow() + OpenFor).UtcTicks);

    private async Task<BoardReadModelResponse?> ReadOnceAsync(string project, CancellationToken ct)
    {
        _models.TryGetValue(project, out var cached);
        var read = await client.GetBoardAsync(project, cached?.ETag, ct);
        switch (read.Status)
        {
            case TaskServerBoardReadStatus.NotModified when cached is not null:
                return cached;
            case TaskServerBoardReadStatus.Ok:
                _models[project] = read.Model!;
                return read.Model;
            case TaskServerBoardReadStatus.ProjectMissing:
                _models.TryRemove(project, out _);
                return null;
            default:
                _models.TryRemove(project, out _);
                return (await client.GetBoardAsync(project, null, ct)).Model;
        }
    }
}
