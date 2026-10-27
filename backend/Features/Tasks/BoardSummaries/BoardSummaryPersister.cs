namespace AgentStudio.Tasks;

/// <summary>
/// The only writer of the persisted board (S5). After each task index
/// publication it builds every live card once, per project, with the same
/// enrichment and serializer as the board routes, and replaces the persisted
/// rows of the projects whose cards changed. It reads the published
/// generation without refreshing it, so an idle index stays idle, and it
/// never runs on a request.
/// <para>
/// Each project is enriched on its own: transitive waiters only name cards of
/// the same project, so a restored board shown to a project-scoped account
/// never names a card the live board would hide from it.
/// </para>
/// </summary>
public sealed class BoardSummaryPersister : BackgroundService
{
    private readonly TaskScannerService _scanner;
    private readonly BoardCardProjector _projector;
    private readonly TaskListGitProjectionCache _gitProjection;
    private readonly TaskTokenUsageCache _tokenCache;
    private readonly BoardSummaryStore _store;
    private readonly RestoredBoard _restored;
    private readonly TimeProvider _clock;
    private readonly ILogger<BoardSummaryPersister> _logger;
    private readonly TimeSpan _interval;
    private readonly Lock _gate = new();
    private Dictionary<string, string>? _persistedHashes;
    private long _persistedGeneration = -1;

    public BoardSummaryPersister(
        TaskScannerService scanner,
        BoardCardProjector projector,
        TaskListGitProjectionCache gitProjection,
        TaskTokenUsageCache tokenCache,
        BoardSummaryStore store,
        RestoredBoard restored,
        TimeProvider clock,
        IConfiguration configuration,
        ILogger<BoardSummaryPersister> logger)
    {
        _scanner = scanner;
        _projector = projector;
        _gitProjection = gitProjection;
        _tokenCache = tokenCache;
        _store = store;
        _restored = restored;
        _clock = clock;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue("BoardSummaries:PersistIntervalSeconds", 10), 1, 3600));
    }

    /// <summary>The index generation last written or confirmed unchanged, or -1.</summary>
    internal long PersistedGeneration
    {
        get { lock (_gate) return _persistedGeneration; }
    }

    /// <summary>
    /// Persists the published generation when it moved since the last call.
    /// Returns the number of projects written or removed; 0 when nothing was
    /// published yet, the generation is unchanged, or no card changed.
    /// </summary>
    public int PersistPublished()
    {
        lock (_gate)
        {
            var publication = _scanner.TryGetPublishedBoard();
            if (publication is null) return 0;
            _restored.Release();
            if (publication.Generation == _persistedGeneration) return 0;

            _persistedHashes ??= _store.Load()
                .ToDictionary(project => project.ProjectName, project => project.ContentHash, StringComparer.Ordinal);
            IReadOnlyList<BoardSummaryProject> built;
            using (_scanner.PinBoardRead(publication))
                built = Build(publication);
            var (changed, removed) = BoardSummaryPolicy.Plan(_persistedHashes, built);
            if (changed.Count + removed.Count > 0)
            {
                // A failed write keeps the generation unpersisted; the next tick retries.
                if (!_store.Write(changed, removed)) return 0;
                foreach (var project in changed) _persistedHashes[project.ProjectName] = project.ContentHash;
                foreach (var name in removed) _persistedHashes.Remove(name);
                _logger.LogInformation(
                    "board-summaries-persisted generation={Generation} written={Written} removed={Removed} cards={Cards}",
                    publication.Generation, changed.Count, removed.Count, built.Sum(project => project.Cards.Count));
            }
            _persistedGeneration = publication.Generation;
            return changed.Count + removed.Count;
        }
    }

    private List<BoardSummaryProject> Build(TaskIndexPublication publication)
    {
        var now = _clock.GetUtcNow();
        var projects = new List<BoardSummaryProject>();
        foreach (var group in publication.Live
                     .Select((card, rank) => (Card: card, Rank: rank))
                     .GroupBy(entry => entry.Card.ProjectName ?? string.Empty, StringComparer.Ordinal))
        {
            var cards = group.Select(entry => entry.Card).ToList();
            // Persistence runs after every publication, including publications
            // caused by unrelated card drift. Keep the ledger fold off this
            // path: a detail read of one card must not trigger token work for
            // every project through the background board writer.
            var cachedTokens = cards.Select(card => (Card: card, Summary: _tokenCache.PeekCached(card)))
                .Where(entry => entry.Summary is not null)
                .ToDictionary(entry => entry.Card.TaskKey, entry => entry.Summary!, StringComparer.Ordinal);
            var enriched = _projector.Project(cards, cards.Where(card => !card.Fixture), cachedTokens);
            var freshness = _gitProjection.ReadFreshness(cards);
            projects.Add(BoardSummaryPolicy.Build(group.Key, publication.Generation, now,
                freshness.GitStateAt, freshness.Stale, enriched, group.Select(entry => entry.Rank).ToList(),
                _projector.LaneSorter(enriched), BoardCardSummary.Serialize));
        }
        return projects;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Load the persisted board before the first read asks for it.
        await Task.Run(_restored.Warm, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                PersistPublished();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "board-summaries-persist-failed");
            }
            try
            {
                await Task.Delay(_interval, _clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
