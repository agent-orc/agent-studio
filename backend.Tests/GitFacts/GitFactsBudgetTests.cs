using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Git;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Deterministic Git facts S1 budgets on the 3,000-card board (AGT-3321): spawn
/// counts, payload bytes, complete per-card index results, duplicate isolation
/// and the index run deadline on an injected clock. Nothing here measures
/// elapsed time; <see cref="GitFactsBenchmarks"/> does that under
/// <c>Category=MachineBound</c>.
/// </summary>
[Collection(GitFactsCollection.Name)]
public sealed class GitFactsBudgetTests(GitFactsBoardFixture board)
{
    /// <summary>Most serialized bytes one card may add to a board read.</summary>
    public const int SummaryBytesPerCard = 2 * 1024;

    [Fact]
    public async Task Board_reads_start_no_git_and_stay_within_two_kib_per_card()
    {
        await using var host = new GitReadCensusHost(board, keepGitStateIndex: true);
        await IndexedAsync(host);

        foreach (var url in new[] { "/api/tasks/", "/api/tasks/grouped" })
        {
            using var response = await host.Get(url);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsByteArrayAsync();
            var cards = Cards(JsonDocument.Parse(body).RootElement).ToList();

            Assert.True(cards.Count >= 900, $"{url} returned only {cards.Count} cards");
            var largest = cards.Max(card => card.GetRawText().Length);
            Assert.True(largest <= SummaryBytesPerCard, $"{url}: one card serializes to {largest} bytes");
            Assert.True(body.Length <= (long)SummaryBytesPerCard * cards.Count,
                $"{url}: {body.Length} bytes for {cards.Count} cards");
        }

        Assert.All(host.Records, record => Assert.Equal(0, record.Spawns));
    }

    [Fact]
    public async Task Persisted_board_summaries_cover_every_live_card_within_two_kib_each()
    {
        var directory = Path.Combine(Path.GetTempPath(), "atp-board-summaries-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = new GitReadCensusHost(board, keepGitStateIndex: true,
                settings: new Dictionary<string, string?> { ["BoardSummaries:StorePath"] = Path.Combine(directory, "board.db") });
            await IndexedAsync(host);
            using (var response = await host.Get("/api/tasks/grouped")) response.EnsureSuccessStatusCode();

            Assert.Equal(1, host.Services.GetRequiredService<BoardSummaryPersister>().PersistPublished());
            var live = host.Services.GetRequiredService<TaskScannerService>().TryGetPublishedBoard()!.Live;
            var cards = host.Services.GetRequiredService<BoardSummaryStore>().Load().SelectMany(project => project.Cards).ToList();

            Assert.Equal(live.Select(card => card.TaskKey).Order(StringComparer.Ordinal),
                cards.Select(card => card.TaskKey).Order(StringComparer.Ordinal));
            var largest = cards.Max(card => card.Summary.Length);
            Assert.True(largest <= SummaryBytesPerCard, $"one persisted card serializes to {largest} bytes");
            Assert.All(host.Records, record => Assert.Equal(0, record.Spawns));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* best-effort temp cleanup */ }
        }
    }

    [Fact]
    public async Task Card_core_and_detail_resources_start_no_git()
    {
        await using var host = new GitReadCensusHost(board, keepGitStateIndex: true);
        await IndexedAsync(host);
        var cards = new[]
        {
            GitFactsBoardFixture.IntegratedReview, GitFactsBoardFixture.EscalatedWithFiles,
            GitFactsBoardFixture.EscalatedBare, GitFactsBoardFixture.InProgress, "dup-1",
            board.PlainCard(TaskStates.Archive), board.PlainCard(TaskStates.Backlog),
        };

        foreach (var id in cards)
        {
            using var core = await host.Get($"/api/tasks/{id}/core?project={host.ProjectId}");
            Assert.True(core.IsSuccessStatusCode, $"core {id}: {(int)core.StatusCode}");
            foreach (var resource in new[] { "overview", "git", "review", "documents", "history", "usage" })
                await host.Get($"/api/tasks/{id}/details/{resource}?project={host.ProjectId}");
        }

        Assert.NotEmpty(host.Records);
        Assert.All(host.Records, record => Assert.True(record.Spawns == 0,
            $"{record.RouteTemplate} started [{string.Join(",", record.Commands)}]"));
    }

    [Fact]
    public async Task Index_publishes_facts_for_every_card_and_isolates_duplicate_ids()
    {
        await using var host = new GitReadCensusHost(board, keepGitStateIndex: true);
        await IndexedAsync(host);
        var scanner = host.Services.GetRequiredService<TaskScannerService>();
        var cache = host.Services.GetRequiredService<TaskListGitProjectionCache>();
        var raw = scanner.ScanAllJobsRaw()
            .Where(task => WatchPathComparison.PathsEqual(task.WatchPath, board.WatchPath))
            .ToArray();
        var cards = TaskProjectionCards.Select(raw);

        Assert.Equal(board.CardCount, raw.Length);
        Assert.Equal(board.CardCount - GitFactsBoardFixture.DuplicateIds, cards.Length);
        Assert.Equal(GitFactsBoardFixture.DuplicateIds, TaskProjectionCards.FindDuplicateIds(raw).Count);
        var notReady = cards.Select(cache.ReadTask).Where(snapshot => snapshot.State != "ready").ToList();
        Assert.True(notReady.Count == 0,
            $"{notReady.Count} cards without published facts, e.g. {notReady.FirstOrDefault()?.TaskKey} "
            + $"({notReady.FirstOrDefault()?.State}/{notReady.FirstOrDefault()?.ReasonCode})");
        Assert.True(cache.ReadTask(cards.Single(card => card.Id == GitFactsBoardFixture.IntegratedReview))
            .Data?.Integration?.Status is "integrated");
    }

    [Fact]
    public async Task Unchanged_input_index_run_starts_no_git_process()
    {
        var runs = new IndexRunLog();
        await using var host = new GitReadCensusHost(board, keepGitStateIndex: true, runs);
        await IndexedAsync(host);
        var indexer = host.Services.GetRequiredService<GitStateIndexService>();

        var before = runs.Completed.Count;
        indexer.RequestRefresh(GitFactsBoardFixture.ProjectName, "task-event");
        var run = await runs.NextAsync(before);

        Assert.Equal("task-event", run.Trigger);
        Assert.Contains(run.Outcome, IndexRunLog.CompleteOutcomes);
        // S1 measured 1 spawn (the config capture); S2 (AGT-3322) moved the
        // configuration generation to the watcher, so the run starts none.
        Assert.Equal(0, run.Spawns);
        Assert.Empty(runs.Failures);
    }

    [Fact]
    public async Task Index_run_deadline_expires_on_the_injected_clock_and_keeps_the_last_snapshot()
    {
        var clock = new FakeTimeProvider(board.Clock.GetUtcNow());
        var config = new ConfigurationBuilder().AddInMemoryCollection(board.HostSettings()).Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var cache = new TaskListGitProjectionCache();
        var calls = 0;
        var hang = new TaskCompletionSource<TaskListGitProjection>();
        Task<TaskListGitProjection> Build(IReadOnlyCollection<TaskInfo> tasks)
            => Interlocked.Increment(ref calls) == 1
                ? Task.FromResult(TaskListGitProjection.Empty with
                {
                    Signatures = tasks.ToDictionary(task => task.TaskKey, TaskGitSignature.For, StringComparer.Ordinal),
                })
                : hang.Task;
        var options = new GitStateIndexOptions(1, TimeSpan.FromMilliseconds(20), TimeSpan.FromHours(1),
            TimeSpan.FromHours(1)) { MaxRetries = 0 };
        Assert.Equal(TimeSpan.FromSeconds(90), options.RunDeadline);
        using var indexer = new GitStateIndexService(scanner,
            new TaskWatcherService(scanner, NullLogger<TaskWatcherService>.Instance, config), cache, Build,
            _ => { }, NullLogger.Instance, options, clock);

        await indexer.StartAsync(CancellationToken.None);
        try
        {
            await Until(() => indexer.GetRepositoryStatuses().SingleOrDefault()?.GitStateAt is not null);
            var published = indexer.GetRepositoryStatuses().Single().GitStateAt;
            var card = TaskProjectionCards.Select(scanner.ScanAllJobsRaw())
                .First(task => task.Id == GitFactsBoardFixture.IntegratedReview);
            Assert.Equal("ready", cache.ReadTask(card).State);

            indexer.RequestRefresh(GitFactsBoardFixture.ProjectName, "manual");
            await Until(() => Volatile.Read(ref calls) == 2);
            clock.Advance(options.RunDeadline - TimeSpan.FromMilliseconds(1));
            Assert.True(indexer.IsRunning(GitFactsBoardFixture.ProjectName));
            clock.Advance(TimeSpan.FromMilliseconds(2));
            await Until(() => indexer.GetRepositoryStatuses().Single().ReasonCode == "timeout");

            var status = indexer.GetRepositoryStatuses().Single();
            Assert.Equal(published, status.GitStateAt);
            Assert.NotNull(cache.ReadTask(card).ComputedAt);
            Assert.Equal("timeout", cache.ReadTask(card).ReasonCode);
        }
        finally { await indexer.StopAsync(CancellationToken.None); }
    }

    /// <summary>Waits for the host's first index publication of the board's repository.</summary>
    internal static async Task IndexedAsync(GitReadCensusHost host)
    {
        var indexer = host.Services.GetRequiredService<GitStateIndexService>();
        await Until(() => indexer.GetRepositoryStatuses()
            .Any(status => status.ProjectName == GitFactsBoardFixture.ProjectName && status.GitStateAt is not null));
    }

    /// <summary>Polls a background condition; the budget only turns a hang into a failure.</summary>
    internal static async Task Until(Func<bool> condition)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            if (waited.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException("Background condition not reached.");
            await Task.Delay(20);
        }
    }

    /// <summary>Every card object in a board body: array items that carry an id and a state.</summary>
    internal static IEnumerable<JsonElement> Cards(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out _)
                        && item.TryGetProperty("state", out _))
                        yield return item;
                    else
                        foreach (var nested in Cards(item)) yield return nested;
                }
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                foreach (var nested in Cards(property.Value))
                    yield return nested;
                break;
        }
    }
}

/// <summary>Captures the index's own <c>git-index-run</c> log lines of one host.</summary>
public sealed class IndexRunLog : ILoggerProvider
{
    private readonly ConcurrentQueue<IndexRun> _completed = new();
    private readonly ConcurrentQueue<string> _failures = new();
    private int _scopeSpawns;

    public sealed record IndexRun(string Trigger, int Spawns, long ElapsedMs, string Outcome);

    /// <summary>
    /// Outcomes of a run that leaves complete per-card facts behind: a new
    /// generation, an unchanged input, or a restored generation the start-up
    /// run confirmed (the fact store of AGT-3322).
    /// </summary>
    public static readonly IReadOnlySet<string> CompleteOutcomes = new HashSet<string>(StringComparer.Ordinal)
        { "published", "unchanged", "confirmed" };

    public IReadOnlyList<IndexRun> Completed => _completed.ToArray();
    public IReadOnlyList<string> Failures => _failures.ToArray();

    public async Task<IndexRun> NextAsync(int afterCount)
    {
        await GitFactsBudgetTests.Until(() => _completed.Count > afterCount);
        return Completed[afterCount];
    }

    public async Task<IndexRun> FirstAsync(Func<IndexRun, bool> match)
    {
        await GitFactsBudgetTests.Until(() => _completed.Any(match));
        return Completed.First(match);
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private sealed class Logger(IndexRunLog log) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is not IReadOnlyList<KeyValuePair<string, object?>> fields) return;
            var template = Field(fields, "{OriginalFormat}")?.ToString() ?? "";
            if (template.StartsWith("git-index-run-failed", StringComparison.Ordinal))
                log._failures.Enqueue(formatter(state, exception));
            // The run's telemetry scope reports every spawn when it closes; the
            // run line that follows counts spawns only for a published run.
            else if (template.StartsWith("git-info request=", StringComparison.Ordinal)
                     && Field(fields, "Label")?.ToString() == "git-index-run")
                Volatile.Write(ref log._scopeSpawns, Convert.ToInt32(Field(fields, "Spawns")));
            else if (template.StartsWith("git-index-run repository=", StringComparison.Ordinal))
                log._completed.Enqueue(new IndexRun(Field(fields, "Trigger")?.ToString() ?? "",
                    Math.Max(Convert.ToInt32(Field(fields, "Spawns")), Interlocked.Exchange(ref log._scopeSpawns, 0)),
                    Convert.ToInt64(Field(fields, "ElapsedMs")),
                    Field(fields, "Outcome")?.ToString() ?? ""));
        }

        private static object? Field(IReadOnlyList<KeyValuePair<string, object?>> fields, string name)
            => fields.FirstOrDefault(field => field.Key == name).Value;
    }
}
