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

/// <summary>
/// One delivered slice of a search. <see cref="Repository"/> is null for the task
/// domain, which is answered in one piece from memory; the git domains emit one
/// chunk per checkout with <see cref="Completed"/> of <see cref="Total"/> so the
/// palette can show how far the sweep has got instead of one opaque spinner.
/// </summary>
public sealed record GlobalSearchChunk(
    string Domain,
    IReadOnlyList<GlobalSearchItem> Items,
    string? Repository,
    int Completed,
    int Total,
    string? Error);

/// <summary>
/// Bounded, read-only workspace search over prebuilt indexes.
///
/// <para>Tasks are matched against <see cref="TaskSearchIndex"/> (memory only, no
/// file reads on the request path); commits and files are matched against the
/// HEAD-keyed <see cref="RepositorySearchIndex"/>, so a new query on an unchanged
/// checkout spawns no git process. Repositories are swept in parallel with the
/// same bounded degree the other read-only git surfaces use, and each one is
/// delivered as it finishes rather than after the slowest.</para>
/// </summary>
public sealed class GlobalSearchService(
    TaskSearchIndex taskIndex,
    RepositorySearchIndex repositoryIndex,
    TaskScannerService scanner,
    ProjectRegistry registry,
    ILogger<GlobalSearchService> logger)
{
    private const int MaxPerDomain = 30;
    private const string DefaultColor = "#6e6e6e";
    private const string DegradedMessage = "Some results could not be loaded.";

    /// <summary>A search past this budget names its slowest repository in a warning.</summary>
    internal const long SlowSearchWarningMs = 5_000;

    /// <summary>Git-backed domains, in the order a repository is swept for them.</summary>
    private static readonly string[] GitDomains = ["commits", "files"];

    /// <summary>
    /// Streams per-domain slices. The task chunk is produced first and never waits
    /// on git; the git chunks arrive in completion order. Cancelling the token
    /// stops the sweep, so a superseded keystroke stops paying for its work.
    /// </summary>
    public async IAsyncEnumerable<GlobalSearchChunk> StreamAsync(
        string query,
        ISet<string> domains,
        int limit,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // A search superseded before it started does no work at all.
        ct.ThrowIfCancellationRequested();
        limit = Math.Clamp(limit, 1, MaxPerDomain);
        var timer = Stopwatch.StartNew();
        var telemetry = new SearchTelemetry();
        var colors = ProjectColors();

        if (domains.Contains("tasks"))
        {
            GlobalSearchChunk chunk;
            try
            {
                chunk = new("tasks", SearchTasks(query, limit, colors), null, 1, 1, null);
            }
            catch (Exception ex)
            {
                chunk = new("tasks", [], null, 1, 1, Degrade("tasks", ex, null));
            }
            telemetry.TasksMs = timer.ElapsedMilliseconds;
            yield return chunk;
        }

        var gitDomains = GitDomains.Where(domains.Contains).ToArray();
        var repositories = Repositories(colors);
        if (gitDomains.Length == 0 || repositories.Count == 0)
        {
            LogCompletion(query, domains, timer, telemetry);
            yield break;
        }

        var channel = Channel.CreateUnbounded<GlobalSearchChunk>();
        var progress = gitDomains.ToDictionary(domain => domain, _ => new int[1], StringComparer.Ordinal);
        _ = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                    repositories,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = ReadOnlyGitConcurrencyLimiter.MaxConcurrency,
                        CancellationToken = ct,
                    },
                    async (repository, token) =>
                    {
                        var started = Stopwatch.GetTimestamp();
                        foreach (var domain in gitDomains)
                        {
                            token.ThrowIfCancellationRequested();
                            var (items, error) = SearchRepository(domain, repository, query, telemetry);
                            var done = Interlocked.Increment(ref progress[domain][0]);
                            await channel.Writer.WriteAsync(
                                new GlobalSearchChunk(domain, items, repository.Name, done, repositories.Count, error),
                                token);
                        }
                        telemetry.NoteRepository(repository.Name, ElapsedMs(started));
                    });
            }
            catch (OperationCanceledException ex)
            {
                SilentCatch.Note(ex, "GlobalSearchService: search cancelled by the caller");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "global-search-sweep-failed");
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
            {
                // The last repository of a domain fixes that domain's wall clock,
                // which is what the operator waited for.
                if (chunk.Completed == chunk.Total) telemetry.NoteDomainDone(chunk.Domain, timer.ElapsedMilliseconds);
                yield return chunk;
            }
        }
        finally
        {
            LogCompletion(query, domains, timer, telemetry);
        }
    }

    /// <summary>
    /// Drains <see cref="StreamAsync"/> into the single-response contract of
    /// <c>GET /api/search</c>. Callers that cannot consume a stream keep the
    /// original shape, including the global per-domain ranking and limit.
    /// </summary>
    public async Task<GlobalSearchResponse> SearchAsync(
        string query, ISet<string> domains, int limit, CancellationToken ct = default)
    {
        var timer = Stopwatch.StartNew();
        limit = Math.Clamp(limit, 1, MaxPerDomain);
        var tasks = new List<GlobalSearchItem>();
        var commits = new List<GlobalSearchItem>();
        var files = new List<GlobalSearchItem>();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var chunk in StreamAsync(query, domains, limit, ct))
        {
            switch (chunk.Domain)
            {
                case "tasks": tasks.AddRange(chunk.Items); break;
                case "commits": commits.AddRange(chunk.Items); break;
                case "files": files.AddRange(chunk.Items); break;
            }
            if (chunk.Error != null) errors[chunk.Domain] = chunk.Error;
        }

        timer.Stop();
        return new(query,
            tasks.Take(limit).ToList(),
            RankItems(commits, query).Take(limit).ToList(),
            RankItems(files, query).Take(limit).ToList(),
            errors, timer.ElapsedMilliseconds);
    }

    private (IReadOnlyList<GlobalSearchItem> Items, string? Error) SearchRepository(
        string domain, SearchRepository repository, string query, SearchTelemetry telemetry)
    {
        try
        {
            if (domain == "commits")
            {
                var commits = repositoryIndex.Commits(repository.Root);
                telemetry.NoteCache(repository.Name, domain, commits.CacheHit);
                return (MatchCommits(commits.Value, repository, query), null);
            }

            var paths = repositoryIndex.Paths(repository.Root);
            telemetry.NoteCache(repository.Name, domain, paths.CacheHit);
            return (MatchFiles(paths.Value, repository, query), null);
        }
        catch (Exception ex)
        {
            return ([], Degrade(domain, ex, repository.Name));
        }
    }

    private List<GlobalSearchItem> SearchTasks(
        string query, int limit, IReadOnlyDictionary<string, string> colors)
    {
        // The blob is already lowercase, so the per-card test is an ordinal
        // Contains over one string instead of four culture-aware comparisons.
        var needle = query.ToLowerInvariant();
        return taskIndex.Entries()
            .Where(entry => entry.Blob.Contains(needle, StringComparison.Ordinal))
            .OrderBy(entry => string.Equals(entry.Task.Key, query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(entry => entry.Task.LastActivity)
            .Take(limit)
            .Select(entry => new GlobalSearchItem("tasks", entry.Task.ProjectName,
                colors.GetValueOrDefault(entry.Task.ProjectName, DefaultColor), entry.Task.Title,
                FirstMatchingLine(entry.Text, query) ?? entry.Task.State, entry.Task.TaskKey, entry.Task.State))
            .ToList();
    }

    internal static List<GlobalSearchItem> MatchCommits(
        IReadOnlyList<CommitIndexEntry> commits, SearchRepository repository, string query) => commits
        .Where(c => Contains(c.Sha, query) || Contains(c.ShortSha, query) || Contains(c.Subject, query))
        .Select(c => new GlobalSearchItem(
            "commits", repository.Name, repository.Color, c.Subject, c.ShortSha, Sha: c.Sha))
        .ToList();

    internal static List<GlobalSearchItem> MatchFiles(
        IReadOnlyList<string> paths, SearchRepository repository, string query) => paths
        .Where(path => Contains(path, query))
        .Select(path => new GlobalSearchItem("files", repository.Name, repository.Color,
            Path.GetFileName(path), path,
            Path: path, IsWiki: path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)
                // docs/app/ is a code contract, not a wiki page: never route it into the wiki viewer.
                && !path.StartsWith("docs/app/", StringComparison.OrdinalIgnoreCase)))
        .ToList();

    internal static IEnumerable<GlobalSearchItem> RankItems(IEnumerable<GlobalSearchItem> items, string query) => items
        .OrderBy(i => string.Equals(i.Title, query, StringComparison.OrdinalIgnoreCase) || string.Equals(i.Subtitle, query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(i => i.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(i => i.Title.Length);

    private Dictionary<string, string> ProjectColors()
    {
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in registry.List().Where(p => !p.Archived))
            colors[project.DisplayName] = project.Color ?? DefaultColor;
        foreach (var watchPath in scanner.GetWatchPaths()) colors.TryAdd(watchPath.Name, DefaultColor);
        return colors;
    }

    private List<SearchRepository> Repositories(IReadOnlyDictionary<string, string> colors) => registry.List()
        .Where(p => !p.Archived)
        .Select(p => (Name: p.DisplayName, Root: (string?)(p.RepositoryPath ?? p.RootPath)))
        .Concat(scanner.GetWatchPaths()
            .Select(p => (Name: p.Name, Root: (string?)(p.RepositoryPath.Length > 0 ? p.RepositoryPath : p.RootPath))))
        .Where(p => !string.IsNullOrWhiteSpace(p.Root) && Directory.Exists(p.Root))
        .GroupBy(p => Path.GetFullPath(p.Root!), StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .Select(p => new SearchRepository(p.Name, p.Root!, colors.GetValueOrDefault(p.Name, DefaultColor)))
        .ToList();

    private string Degrade(string domain, Exception ex, string? project)
    {
        logger.LogWarning(ex, "global-search-domain-failed domain={Domain} project={Project}", domain, project);
        return DegradedMessage;
    }

    private void LogCompletion(string query, ISet<string> domains, Stopwatch timer, SearchTelemetry telemetry)
    {
        timer.Stop();
        logger.LogInformation(
            "global-search-completed queryLength={QueryLength} domains={Domains} tasksMs={TasksMs} commitsMs={CommitsMs} filesMs={FilesMs} cache={Cache} indexRebuilds={Rebuilds} cardReads={CardReads} durationMs={DurationMs}",
            query.Length, string.Join(',', domains), telemetry.TasksMs, telemetry.CommitsMs, telemetry.FilesMs,
            telemetry.CacheReport(), taskIndex.Rebuilds, taskIndex.CardReads, timer.ElapsedMilliseconds);

        if (timer.ElapsedMilliseconds <= SlowSearchWarningMs) return;
        var (name, elapsed) = telemetry.Slowest();
        logger.LogWarning(
            "global-search-slow durationMs={DurationMs} slowestRepository={Repository} slowestMs={SlowestMs} commitWindow={CommitWindow}",
            timer.ElapsedMilliseconds, name ?? "n/a", elapsed, RepositorySearchIndex.CommitWindow);
    }

    private static long ElapsedMs(long startedTimestamp) =>
        (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;

    private static bool Contains(string? value, string query) => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private static string? FirstMatchingLine(string text, string query) =>
        text.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => Contains(x, query));

    /// <summary>
    /// Per-domain timings and per-repository cache outcomes, gathered from the
    /// parallel sweep so the completion log can explain a slow search instead of
    /// reporting one aggregate number.
    /// </summary>
    private sealed class SearchTelemetry
    {
        private readonly Lock _lock = new();
        private readonly SortedDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
        private string? _slowestRepository;
        private long _slowestMs;

        public long TasksMs;
        public long CommitsMs;
        public long FilesMs;

        public void NoteDomainDone(string domain, long elapsedMs)
        {
            lock (_lock)
            {
                if (domain == "commits") CommitsMs = elapsedMs;
                else if (domain == "files") FilesMs = elapsedMs;
            }
        }

        public void NoteCache(string repository, string domain, bool hit)
        {
            lock (_lock) _cache[$"{repository}/{domain}"] = hit ? "hit" : "miss";
        }

        public void NoteRepository(string repository, long elapsedMs)
        {
            lock (_lock)
            {
                if (elapsedMs <= _slowestMs) return;
                _slowestMs = elapsedMs;
                _slowestRepository = repository;
            }
        }

        public (string? Repository, long ElapsedMs) Slowest()
        {
            lock (_lock) return (_slowestRepository, _slowestMs);
        }

        public string CacheReport()
        {
            lock (_lock) return string.Join(',', _cache.Select(pair => $"{pair.Key}={pair.Value}"));
        }
    }
}
