using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace AgentStudio.Search;

public sealed record GlobalSearchItem(
    string Domain,
    string ProjectName,
    string ProjectColor,
    string Title,
    string Subtitle,
    string? TaskKey = null,
    string? Lane = null,
    string? Sha = null,
    string? Path = null,
    bool IsWiki = false);

public sealed record GlobalSearchResponse(
    string Query,
    IReadOnlyList<GlobalSearchItem> Tasks,
    IReadOnlyList<GlobalSearchItem> Commits,
    IReadOnlyList<GlobalSearchItem> Files,
    IReadOnlyDictionary<string, string> Errors,
    long DurationMs);

/// <summary>A registered repository the git domains search, with its board colour.</summary>
public sealed record GlobalSearchRepository(string Name, string Root, string Color);

/// <summary>
/// One frame of the streamed search. <see cref="Name"/> is the SSE event name
/// (<c>meta</c>, <c>tasks</c>, <c>repository</c>, <c>done</c>); the payload is
/// one of the frame records below, serialized as the event data.
/// </summary>
public sealed record GlobalSearchStreamEvent(string Name, object Payload);

/// <summary>Opens the stream: the normalized query and how many repositories
/// the git domains will visit, so the palette can render "i of n" from the
/// first frame.</summary>
public sealed record GlobalSearchMetaFrame(string Query, int Repositories);

/// <summary>Task matches from the warm index. Always precedes any repository frame.</summary>
public sealed record GlobalSearchTasksFrame(
    IReadOnlyList<GlobalSearchItem> Items, long DurationMs, string? Error);

/// <summary>One repository's git matches, emitted in completion order.
/// <paramref name="Index"/> counts completions, not registration order. Errors
/// are per domain so a failing commit walk never blanks out the file results
/// that did arrive.</summary>
public sealed record GlobalSearchRepositoryFrame(
    string Name,
    int Index,
    int Total,
    IReadOnlyList<GlobalSearchItem> Commits,
    IReadOnlyList<GlobalSearchItem> Files,
    long DurationMs,
    string CommitsCache,
    string FilesCache,
    string? CommitsError,
    string? FilesError);

/// <summary>Closes the stream.</summary>
public sealed record GlobalSearchDoneFrame(long DurationMs, IReadOnlyDictionary<string, string> Errors);

/// <summary>
/// Bounded, read-only workspace search over <see cref="GlobalSearchIndex"/>.
///
/// <para>Nothing on the request path reads a card or spawns a git process: the
/// index materializes each domain once per revision and every query after that
/// is an in-memory scan. Repositories are searched in parallel with a bounded
/// degree, and <see cref="StreamAsync"/> hands each repository to the palette
/// the moment it finishes rather than waiting for the slowest one.</para>
/// </summary>
public sealed class GlobalSearchService(
    TaskScannerService scanner,
    GlobalSearchIndex index,
    ProjectRegistry registry,
    ILogger<GlobalSearchService> logger)
{
    private const int MaxPerDomain = 30;
    private const string DefaultColor = "#6e6e6e";

    /// <summary>A search past this budget names its slowest repository in a warning.</summary>
    internal const int SlowSearchMs = 5_000;

    private static readonly int MaxRepositoryConcurrency = Math.Clamp(Environment.ProcessorCount, 2, 8);

    /// <summary>
    /// Single-response search. Kept for <c>GET /api/search</c>, whose wire
    /// contract is unchanged; the palette uses <see cref="StreamAsync"/>.
    /// </summary>
    public GlobalSearchResponse Search(string query, ISet<string> domains, int limit, CancellationToken ct = default)
    {
        var timer = Stopwatch.StartNew();
        limit = Math.Clamp(limit, 1, MaxPerDomain);
        var colors = BuildColors();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tasks = new List<GlobalSearchItem>();
        var commits = new List<GlobalSearchItem>();
        var files = new List<GlobalSearchItem>();

        long taskMs = 0;
        if (domains.Contains("tasks"))
        {
            var taskTimer = Stopwatch.StartNew();
            try { tasks = MatchTasks(index.GetTasks(), query, limit, colors); }
            catch (Exception ex) { Degrade("tasks", ex, errors); }
            taskMs = taskTimer.ElapsedMilliseconds;
        }

        var results = new List<RepositorySearchResult>();
        var gitTimer = Stopwatch.StartNew();
        if (domains.Contains("commits") || domains.Contains("files"))
        {
            Parallel.ForEach(
                ResolveRepositories(colors),
                new ParallelOptions { MaxDegreeOfParallelism = MaxRepositoryConcurrency, CancellationToken = ct },
                repository =>
                {
                    var result = SearchRepository(repository, query, limit, domains, ct);
                    lock (results) results.Add(result);
                });
        }
        var gitMs = gitTimer.ElapsedMilliseconds;

        foreach (var result in results)
        {
            commits.AddRange(result.Commits);
            files.AddRange(result.Files);
            foreach (var (domain, message) in result.Errors) errors[domain] = message;
        }

        timer.Stop();
        LogCompletion(query, domains, tasks.Count, commits.Count, files.Count, errors, results, taskMs, gitMs, timer.ElapsedMilliseconds);
        return new(query,
            tasks,
            TakeRanked(commits, query, limit),
            TakeRanked(files, query, limit),
            errors, timer.ElapsedMilliseconds);
    }

    /// <summary>
    /// Streamed search. Emits <c>meta</c>, then <c>tasks</c> from the warm index
    /// (the operator sees something within a frame), then one <c>repository</c>
    /// frame per repository in completion order carrying its own progress
    /// counter, then <c>done</c>. Cancelling the enumeration kills the git
    /// children still running.
    /// </summary>
    public async IAsyncEnumerable<GlobalSearchStreamEvent> StreamAsync(
        string query,
        ISet<string> domains,
        int limit,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var timer = Stopwatch.StartNew();
        limit = Math.Clamp(limit, 1, MaxPerDomain);
        var colors = BuildColors();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var searchesGit = domains.Contains("commits") || domains.Contains("files");
        var repositories = searchesGit ? ResolveRepositories(colors) : [];

        yield return new("meta", new GlobalSearchMetaFrame(query, repositories.Count));

        long taskMs = 0;
        var taskCount = 0;
        if (domains.Contains("tasks"))
        {
            // Every stage boundary re-checks: an abandoned query must stop at
            // the next stage rather than run the rest of the search for nobody.
            ct.ThrowIfCancellationRequested();
            var taskTimer = Stopwatch.StartNew();
            var items = new List<GlobalSearchItem>();
            try { items = MatchTasks(index.GetTasks(), query, limit, colors); }
            catch (Exception ex) { Degrade("tasks", ex, errors); }
            taskMs = taskTimer.ElapsedMilliseconds;
            taskCount = items.Count;
            yield return new("tasks", new GlobalSearchTasksFrame(items, taskMs, errors.GetValueOrDefault("tasks")));
        }

        var completed = new List<RepositorySearchResult>();
        var commitCount = 0;
        var fileCount = 0;
        var gitTimer = Stopwatch.StartNew();
        if (repositories.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var channel = Channel.CreateUnbounded<RepositorySearchResult>(
                new UnboundedChannelOptions { SingleReader = true });
            var producer = ProduceRepositoryResultsAsync(channel.Writer, repositories, query, limit, domains, ct);

            var position = 0;
            await foreach (var result in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                completed.Add(result);
                commitCount += result.Commits.Count;
                fileCount += result.Files.Count;
                foreach (var (domain, message) in result.Errors) errors[domain] = message;
                position++;
                yield return new("repository", new GlobalSearchRepositoryFrame(
                    result.Repository.Name,
                    position,
                    repositories.Count,
                    result.Commits,
                    result.Files,
                    result.DurationMs,
                    CacheLabel(result.CommitsCacheHit),
                    CacheLabel(result.FilesCacheHit),
                    result.Errors.GetValueOrDefault("commits"),
                    result.Errors.GetValueOrDefault("files")));
            }
            await producer.ConfigureAwait(false);
        }
        var gitMs = gitTimer.ElapsedMilliseconds;

        timer.Stop();
        LogCompletion(query, domains, taskCount, commitCount, fileCount, errors, completed, taskMs, gitMs, timer.ElapsedMilliseconds);
        yield return new("done", new GlobalSearchDoneFrame(timer.ElapsedMilliseconds, errors));
    }

    private async Task ProduceRepositoryResultsAsync(
        ChannelWriter<RepositorySearchResult> writer,
        IReadOnlyList<GlobalSearchRepository> repositories,
        string query,
        int limit,
        ISet<string> domains,
        CancellationToken ct)
    {
        try
        {
            await Parallel.ForEachAsync(
                repositories,
                new ParallelOptions { MaxDegreeOfParallelism = MaxRepositoryConcurrency, CancellationToken = ct },
                (repository, token) =>
                {
                    writer.TryWrite(SearchRepository(repository, query, limit, domains, token));
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
            writer.Complete();
        }
        catch (Exception ex)
        {
            // Completing with the fault hands it to the consumer's await
            // instead of losing it on an orphaned producer task.
            writer.Complete(ex);
        }
    }

    /// <summary>Searches one repository's indexed domains. Never throws for a
    /// broken repository: it degrades that repository and reports the error.</summary>
    private RepositorySearchResult SearchRepository(
        GlobalSearchRepository repository, string query, int limit, ISet<string> domains, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var lowered = query.ToLowerInvariant();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<GlobalSearchItem> commits = [];
        List<GlobalSearchItem> files = [];
        bool? commitsCacheHit = null;
        bool? filesCacheHit = null;

        if (domains.Contains("commits"))
        {
            try
            {
                var (indexed, stats) = index.GetCommits(repository.Root, ct);
                commitsCacheHit = stats.CacheHit;
                commits = MatchCommits(indexed, lowered, query, repository, limit);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Degrade("commits", ex, errors, repository.Name); }
        }

        if (domains.Contains("files"))
        {
            try
            {
                var (indexed, stats) = index.GetFiles(repository.Root, ct);
                filesCacheHit = stats.CacheHit;
                files = MatchFiles(indexed, query, repository, limit);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Degrade("files", ex, errors, repository.Name); }
        }

        return new(repository, commits, files, timer.ElapsedMilliseconds, commitsCacheHit, filesCacheHit, errors);
    }

    internal static List<GlobalSearchItem> MatchTasks(
        IReadOnlyList<IndexedTask> tasks, string query, int limit, IReadOnlyDictionary<string, string> colors)
    {
        var lowered = query.ToLowerInvariant();
        return tasks
            .Where(entry => entry.Haystack.Contains(lowered, StringComparison.Ordinal))
            .OrderBy(entry => string.Equals(entry.Task.Key, query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(entry => entry.Task.LastActivity)
            .Take(limit)
            .Select(entry => new GlobalSearchItem("tasks", entry.Task.ProjectName,
                colors.GetValueOrDefault(entry.Task.ProjectName, DefaultColor), entry.Task.Title,
                FirstMatchingLine(entry.Text, query) ?? entry.Task.State, entry.Task.TaskKey, entry.Task.State))
            .ToList();
    }

    internal static List<GlobalSearchItem> MatchCommits(
        IReadOnlyList<IndexedCommit> commits, string lowered, string query, GlobalSearchRepository repository, int limit) =>
        TakeRanked(
            commits
                .Where(commit => commit.Haystack.Contains(lowered, StringComparison.Ordinal))
                .Select(commit => new GlobalSearchItem(
                    "commits", repository.Name, repository.Color, commit.Subject, commit.ShortSha, Sha: commit.Sha)),
            query, limit);

    internal static List<GlobalSearchItem> MatchFiles(
        IReadOnlyList<string> paths, string query, GlobalSearchRepository repository, int limit) =>
        TakeRanked(
            paths
                .Where(path => path.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(path => new GlobalSearchItem(
                    "files", repository.Name, repository.Color, System.IO.Path.GetFileName(path), path,
                    Path: path, IsWiki: path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)
                        // docs/app/ is a code contract, not a wiki page: never route it into the wiki viewer.
                        && !path.StartsWith("docs/app/", StringComparison.OrdinalIgnoreCase))),
            query, limit);

    internal static IEnumerable<GlobalSearchItem> RankItems(IEnumerable<GlobalSearchItem> items, string query) =>
        items.OrderBy(item => RankOf(item, query));

    /// <summary>
    /// Keeps only the best <paramref name="limit"/> items, in rank order,
    /// without sorting the full match set. A two-character query can match most
    /// paths in a large repository; sorting that is the difference between a
    /// palette that answers and one that stalls.
    /// </summary>
    internal static List<GlobalSearchItem> TakeRanked(IEnumerable<GlobalSearchItem> items, string query, int limit)
    {
        var best = new List<(RankKey Key, GlobalSearchItem Item)>(limit + 1);
        foreach (var item in items)
        {
            var key = RankOf(item, query);
            if (best.Count >= limit && key.CompareTo(best[^1].Key) >= 0) continue;
            var at = best.FindIndex(entry => key.CompareTo(entry.Key) < 0);
            best.Insert(at < 0 ? best.Count : at, (key, item));
            if (best.Count > limit) best.RemoveAt(best.Count - 1);
        }
        return best.Select(entry => entry.Item).ToList();
    }

    private static RankKey RankOf(GlobalSearchItem item, string query) => new(
        string.Equals(item.Title, query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Subtitle, query, StringComparison.OrdinalIgnoreCase) ? 0 : 1,
        item.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1,
        item.Title.Length);

    /// <summary>Exact match first, then prefix match, then the shortest title.</summary>
    internal readonly record struct RankKey(int Exact, int Prefix, int Length) : IComparable<RankKey>
    {
        public int CompareTo(RankKey other)
        {
            if (Exact != other.Exact) return Exact - other.Exact;
            if (Prefix != other.Prefix) return Prefix - other.Prefix;
            return Length - other.Length;
        }
    }

    private Dictionary<string, string> BuildColors()
    {
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in registry.List().Where(project => !project.Archived))
            colors[project.DisplayName] = project.Color ?? DefaultColor;
        foreach (var watchPath in scanner.GetWatchPaths()) colors.TryAdd(watchPath.Name, DefaultColor);
        return colors;
    }

    /// <summary>Registered projects plus configured watch paths, deduplicated by
    /// resolved repository root, in a stable order.</summary>
    private List<GlobalSearchRepository> ResolveRepositories(IReadOnlyDictionary<string, string> colors) => registry
        .List().Where(project => !project.Archived)
        .Select(project => (Name: project.DisplayName, Root: project.RepositoryPath ?? project.RootPath ?? ""))
        .Concat(scanner.GetWatchPaths()
            .Select(path => (path.Name, Root: path.RepositoryPath.Length > 0 ? path.RepositoryPath : path.RootPath)))
        .Where(project => !string.IsNullOrWhiteSpace(project.Root) && Directory.Exists(project.Root))
        .GroupBy(project => System.IO.Path.GetFullPath(project.Root), StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .Select(project => new GlobalSearchRepository(
            project.Name, project.Root, colors.GetValueOrDefault(project.Name, DefaultColor)))
        .ToList();

    private void LogCompletion(
        string query,
        ISet<string> domains,
        int tasks,
        int commits,
        int files,
        IReadOnlyDictionary<string, string> errors,
        IReadOnlyCollection<RepositorySearchResult> results,
        long taskMs,
        long gitMs,
        long durationMs)
    {
        var cacheByRepository = string.Join(',', results.Select(result =>
            $"{result.Repository.Name}={CacheLabel(result.CommitsCacheHit)}/{CacheLabel(result.FilesCacheHit)}"));
        logger.LogInformation(
            "global-search-completed queryLength={QueryLength} domains={Domains} tasks={Tasks} commits={Commits} files={Files} errors={Errors} taskMs={TaskMs} gitMs={GitMs} repositories={Repositories} cache={Cache} durationMs={DurationMs}",
            query.Length, string.Join(',', domains), tasks, commits, files, errors.Count,
            taskMs, gitMs, results.Count, cacheByRepository, durationMs);

        if (durationMs <= SlowSearchMs) return;
        var slowest = results.OrderByDescending(result => result.DurationMs).FirstOrDefault();
        logger.LogWarning(
            "global-search-slow durationMs={DurationMs} budgetMs={BudgetMs} slowestRepository={Repository} slowestMs={SlowestMs} cache={Cache}",
            durationMs, SlowSearchMs, slowest?.Repository.Name ?? "(none)", slowest?.DurationMs ?? 0, cacheByRepository);
    }

    private static string CacheLabel(bool? hit) => hit switch
    {
        true => "hit",
        false => "miss",
        _ => "skipped",
    };

    private void Degrade(string domain, Exception ex, IDictionary<string, string> errors, string? project = null)
    {
        errors[domain] = "Some results could not be loaded.";
        logger.LogWarning(ex, "global-search-domain-failed domain={Domain} project={Project}", domain, project);
    }

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private static string? FirstMatchingLine(string text, string query) =>
        text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => Contains(line, query));

    private sealed record RepositorySearchResult(
        GlobalSearchRepository Repository,
        List<GlobalSearchItem> Commits,
        List<GlobalSearchItem> Files,
        long DurationMs,
        bool? CommitsCacheHit,
        bool? FilesCacheHit,
        IReadOnlyDictionary<string, string> Errors);
}
