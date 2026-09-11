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
    bool IsWiki = false,
    string? DossierKey = null,
    string? WorkbenchId = null,
    string? Phase = null,
    DateTime? UpdatedAt = null,
    string? ReferenceKey = null,
    string? ProjectId = null);

public sealed record GlobalSearchResponse(
    string Query,
    IReadOnlyList<GlobalSearchItem> Tasks,
    IReadOnlyList<GlobalSearchItem> Dossiers,
    IReadOnlyList<GlobalSearchItem> Wiki,
    IReadOnlyList<GlobalSearchItem> Commits,
    IReadOnlyList<GlobalSearchItem> Files,
    IReadOnlyDictionary<string, string> Errors,
    long DurationMs);

/// <summary>A repository the git domains fan out over.</summary>
public sealed record GlobalSearchTarget(string Name, string Root, string Color);

/// <summary>One repository's finished contribution to a search.</summary>
public sealed record GlobalSearchRepositoryResult(
    GlobalSearchTarget Target,
    IReadOnlyList<GlobalSearchItem> Commits,
    IReadOnlyList<GlobalSearchItem> Files,
    long CommitsMs,
    long FilesMs,
    bool CommitsFromCache,
    bool FilesFromCache,
    IReadOnlyList<string> FailedDomains)
{
    public long DurationMs => CommitsMs + FilesMs;
}

/// <summary>
/// One frame of a streamed search. <see cref="Payload"/> is one of the frame
/// records below, serialized with the application's HTTP JSON options so the
/// wire shape is camelCase like every other endpoint.
/// </summary>
public sealed record GlobalSearchStreamEvent(string Event, object Payload);

/// <summary>The <c>tasks</c> frame: memory-only matches, emitted before any git work starts.</summary>
public sealed record GlobalSearchTasksFrame(
    IReadOnlyList<GlobalSearchItem> Items, long DurationMs, string? Error);

/// <summary>
/// The <c>dossiers</c> frame: Dossier catalogue matches, read through the
/// shared catalogue rather than a repository fan-out, so it lands alongside the
/// task frame instead of waiting on git.
/// </summary>
public sealed record GlobalSearchDossiersFrame(
    IReadOnlyList<GlobalSearchItem> Items, long DurationMs, string? Error);

/// <summary>The <c>wiki</c> frame: title and heading matches from the Wiki index.</summary>
public sealed record GlobalSearchWikiFrame(
    IReadOnlyList<GlobalSearchItem> Items, long DurationMs, string? Error);

/// <summary>The <c>progress</c> frame: how many repositories this search will visit.</summary>
public sealed record GlobalSearchProgressFrame(int Completed, int Total);

/// <summary>The <c>repository</c> frame: one checkout's matches, emitted when it finishes.</summary>
public sealed record GlobalSearchRepositoryFrame(
    string ProjectName,
    int Completed,
    int Total,
    IReadOnlyList<GlobalSearchItem> Commits,
    IReadOnlyList<GlobalSearchItem> Files,
    long DurationMs,
    bool FromCache,
    IReadOnlyList<string> FailedDomains);

/// <summary>The terminal <c>done</c> frame.</summary>
public sealed record GlobalSearchDoneFrame(
    long DurationMs, long TasksMs, long RepositoriesMs, int Repositories);

/// <summary>
/// Bounded, read-only workspace search over the corpora held by
/// <see cref="GlobalSearchIndexes"/>.
///
/// <para>Repositories are searched in parallel and delivered as they finish:
/// <see cref="StreamAsync"/> emits the non-git domains first and then one frame
/// per repository, so the palette is never
/// blocked by the slowest checkout. <see cref="Search"/> keeps the original
/// single-response contract for callers that want one JSON body.</para>
/// </summary>
public sealed class GlobalSearchService(
    TaskScannerService scanner,
    GlobalSearchIndexes indexes,
    ProjectRegistry registry,
    ProjectDocsService docs,
    ILogger<GlobalSearchService> logger,
    WikiSearchService? wikiSearch = null,
    WorkbenchCatalogueService? workbenchCatalogue = null)
{
    private const int MaxPerDomain = 30;
    private const string DefaultColor = "#6e6e6e";

    /// <summary>A search slower than this names its slowest repository at Warning.</summary>
    private const long SlowSearchWarnMs = 5_000;

    /// <summary>
    /// Concurrent repositories. Each one is a short git spawn plus an in-memory
    /// scan, so the ceiling is about keeping a cold search off a single-file
    /// queue without handing the host fifteen simultaneous git processes.
    /// </summary>
    private static readonly int Fanout = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

    private static readonly string[] GitDomains = ["commits", "files"];

    /// <summary>Single-response search. Every requested domain is complete when this returns.</summary>
    public GlobalSearchResponse Search(string query, ISet<string> domains, int limit)
    {
        var timer = Stopwatch.StartNew();
        limit = Math.Clamp(limit, 1, MaxPerDomain);
        var colors = ResolveColors();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var tasksTimer = Stopwatch.StartNew();
        var tasks = domains.Contains("tasks") ? SearchTasks(query, limit, colors, errors) : [];
        tasksTimer.Stop();

        var dossiersTimer = Stopwatch.StartNew();
        var dossiers = domains.Contains("dossiers") ? SearchDossiers(query, limit, colors, errors) : [];
        dossiersTimer.Stop();

        var wikiTimer = Stopwatch.StartNew();
        var wiki = domains.Contains("wiki") ? SearchWiki(query, limit, colors, errors) : [];
        wikiTimer.Stop();

        var targets = domains.Overlaps(GitDomains) ? ResolveTargets(colors) : [];
        var results = new List<GlobalSearchRepositoryResult>(targets.Count);
        var repositoriesTimer = Stopwatch.StartNew();
        if (targets.Count > 0)
        {
            Parallel.ForEach(targets, new ParallelOptions { MaxDegreeOfParallelism = Fanout }, target =>
            {
                var result = SearchRepository(target, query, domains);
                lock (results) results.Add(result);
            });
        }
        repositoriesTimer.Stop();

        foreach (var domain in results.SelectMany(r => r.FailedDomains).Distinct(StringComparer.Ordinal))
            errors[domain] = "Some results could not be loaded.";

        timer.Stop();
        RecordCompletion(query, domains, tasks.Count, dossiers.Count, wiki.Count, results, tasksTimer.ElapsedMilliseconds,
            dossiersTimer.ElapsedMilliseconds, wikiTimer.ElapsedMilliseconds, repositoriesTimer.ElapsedMilliseconds, timer.ElapsedMilliseconds);
        return new GlobalSearchResponse(query,
            tasks,
            dossiers,
            wiki,
            RankItems(results.SelectMany(r => r.Commits), query).Take(limit).ToList(),
            RankItems(results.SelectMany(r => r.Files), query).Take(limit).ToList(),
            errors, timer.ElapsedMilliseconds);
    }

    /// <summary>
    /// Per-domain delivery. Emits <c>tasks</c>, then <c>progress</c> announcing
    /// how many repositories are in flight, then one <c>repository</c> frame per
    /// checkout as it finishes, then <c>done</c>. Cancelling
    /// <paramref name="ct"/> stops scheduling further repositories.
    /// </summary>
    public async IAsyncEnumerable<GlobalSearchStreamEvent> StreamAsync(
        string query, ISet<string> domains, int limit, [EnumeratorCancellation] CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        limit = Math.Clamp(limit, 1, MaxPerDomain);
        var colors = ResolveColors();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var tasksTimer = Stopwatch.StartNew();
        var tasks = domains.Contains("tasks") ? SearchTasks(query, limit, colors, errors) : [];
        tasksTimer.Stop();
        if (domains.Contains("tasks"))
            yield return new GlobalSearchStreamEvent("tasks", new GlobalSearchTasksFrame(
                tasks, tasksTimer.ElapsedMilliseconds, errors.GetValueOrDefault("tasks")));

        // Dossiers read the shared catalogue, not a git search target, so this
        // frame lands alongside tasks instead of waiting on the git fan-out.
        var dossiersTimer = Stopwatch.StartNew();
        var dossiers = domains.Contains("dossiers") ? SearchDossiers(query, limit, colors, errors) : [];
        dossiersTimer.Stop();
        if (domains.Contains("dossiers"))
            yield return new GlobalSearchStreamEvent("dossiers", new GlobalSearchDossiersFrame(
                dossiers, dossiersTimer.ElapsedMilliseconds, errors.GetValueOrDefault("dossiers")));

        var wikiTimer = Stopwatch.StartNew();
        var wiki = domains.Contains("wiki") ? SearchWiki(query, limit, colors, errors) : [];
        wikiTimer.Stop();
        if (domains.Contains("wiki"))
            yield return new GlobalSearchStreamEvent("wiki", new GlobalSearchWikiFrame(
                wiki, wikiTimer.ElapsedMilliseconds, errors.GetValueOrDefault("wiki")));

        // A palette that already moved on must not pay for the repository scan.
        ct.ThrowIfCancellationRequested();
        var targets = domains.Overlaps(GitDomains) ? ResolveTargets(colors) : [];
        yield return new GlobalSearchStreamEvent("progress", new GlobalSearchProgressFrame(0, targets.Count));

        var repositoriesTimer = Stopwatch.StartNew();
        var results = new List<GlobalSearchRepositoryResult>(targets.Count);
        var completed = 0;
        var commitBudget = limit;
        var fileBudget = limit;

        var channel = Channel.CreateUnbounded<GlobalSearchRepositoryResult>();
        var fanout = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                    targets,
                    new ParallelOptions { MaxDegreeOfParallelism = Fanout, CancellationToken = ct },
                    async (target, token) =>
                    {
                        var result = SearchRepository(target, query, domains);
                        await channel.Writer.WriteAsync(result, token);
                    });
            }
            catch (OperationCanceledException)
            {
                // The operator typed on or closed the palette. Stop scheduling
                // repositories; whatever already streamed stays valid.
                logger.LogDebug("global-search-cancelled completed={Completed} total={Total}", completed, targets.Count);
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, CancellationToken.None);

        await foreach (var result in channel.Reader.ReadAllAsync(ct))
        {
            results.Add(result);
            completed++;
            // The palette appends without reordering what the operator can
            // already see, so each frame carries only this repository's best
            // matches, trimmed against what earlier frames already spent.
            var commits = result.Commits.Take(commitBudget).ToList();
            var files = result.Files.Take(fileBudget).ToList();
            commitBudget -= commits.Count;
            fileBudget -= files.Count;
            yield return new GlobalSearchStreamEvent("repository", new GlobalSearchRepositoryFrame(
                result.Target.Name, completed, targets.Count, commits, files, result.DurationMs,
                result is { CommitsFromCache: true, FilesFromCache: true }, result.FailedDomains));
        }

        await fanout;
        repositoriesTimer.Stop();
        timer.Stop();
        RecordCompletion(query, domains, tasks.Count, dossiers.Count, wiki.Count, results, tasksTimer.ElapsedMilliseconds,
            dossiersTimer.ElapsedMilliseconds, wikiTimer.ElapsedMilliseconds, repositoriesTimer.ElapsedMilliseconds, timer.ElapsedMilliseconds);

        yield return new GlobalSearchStreamEvent("done", new GlobalSearchDoneFrame(
            timer.ElapsedMilliseconds, tasksTimer.ElapsedMilliseconds,
            repositoriesTimer.ElapsedMilliseconds, completed));
    }

    /// <summary>
    /// Task matches from the in-memory index. The card list is the cached task
    /// snapshot and the prompt/status text is the cached blob, so a warm query
    /// reads no file.
    /// </summary>
    private List<GlobalSearchItem> SearchTasks(
        string query, int limit, IReadOnlyDictionary<string, string> colors, IDictionary<string, string> errors)
    {
        try
        {
            var cards = scanner.ScanAllAutomationJobsWithArchive();
            indexes.PruneTaskText(cards);
            return cards
                // Text last: a card matched by key, title, or lane never needs
                // its blob, which on a cold index is a file read.
                .Where(task => KeyContains(task.Key, query) || Contains(task.Title, query)
                               || Contains(task.State, query) || Contains(indexes.TaskText(task), query))
                .OrderBy(task => KeyEquals(task.Key, query) ? 0 : 1)
                .ThenByDescending(task => task.LastActivity)
                .Take(limit)
                .Select(task => new GlobalSearchItem("tasks", task.ProjectName,
                    colors.GetValueOrDefault(task.ProjectName, DefaultColor), task.Title,
                    FirstMatchingLine(indexes.TaskText(task), query) ?? task.State, task.TaskKey, task.State,
                    ReferenceKey: task.Key, ProjectId: ProjectId(task.ProjectName)))
                .ToList();
        }
        catch (Exception ex)
        {
            errors["tasks"] = "Some results could not be loaded.";
            logger.LogWarning(ex, "global-search-domain-failed domain={Domain}", "tasks");
            return [];
        }
    }

    /// <summary>
    /// Dossier matches from the cached Wiki catalogue, one project at a time.
    /// Every registered, non-archived project's history-inclusive catalogue is
    /// searched (so an archived Dossier still surfaces from a live project),
    /// but an archived project itself never contributes: it is not a key of
    /// <paramref name="colors"/>.
    /// </summary>
    private List<GlobalSearchItem> SearchDossiers(
        string query, int limit, IReadOnlyDictionary<string, string> colors, IDictionary<string, string> errors)
    {
        try
        {
            var matches = new List<(string Project, WorkbenchListItem Item)>();
            foreach (var project in colors.Keys)
            {
                // Use the same catalogue service as GET /workbenches. It owns
                // descriptor discovery and freshness, so global search neither
                // snapshots the list at startup nor grows a second scanner.
                var catalogue = workbenchCatalogue?.List(project, includeHistory: true)
                    ?? docs.GetWikiWorkbenchCatalogue(project, includeHistory: true);
                if (catalogue == null) continue;
                matches.AddRange(catalogue.Items.Where(item => item.Valid).Select(item => (project, item)));
            }
            return matches
                .Where(match => DossierMatches(match.Item, query))
                .OrderBy(match => DossierRank(match.Item, query))
                .ThenBy(match => match.Item.Title.Length)
                .Take(limit)
                .Select(match => new GlobalSearchItem("dossiers", match.Project,
                    colors.GetValueOrDefault(match.Project, DefaultColor), match.Item.Title, match.Item.Summary,
                    DossierKey: match.Item.Key, WorkbenchId: match.Item.Id, Lane: match.Item.Status,
                    Phase: match.Item.Phase, UpdatedAt: match.Item.UpdatedAtUtc,
                    ReferenceKey: match.Item.Key, ProjectId: ProjectId(match.Project)))
                .ToList();
        }
        catch (Exception ex)
        {
            errors["dossiers"] = "Some results could not be loaded.";
            logger.LogWarning(ex, "global-search-domain-failed domain={Domain}", "dossiers");
            return [];
        }
    }

    /// <summary>Exact key match first, then title, then summary, then a bare id/status/phase hit.</summary>
    private static int DossierRank(WorkbenchListItem item, string query) =>
        KeyEquals(item.Key, query) ? 0
        : Contains(item.Title, query) ? 1
        : Contains(item.Summary, query) ? 2
        : 3;

    internal static bool DossierMatches(WorkbenchListItem item, string query) =>
        KeyContains(item.Key, query) || Contains(item.Id, query)
        || Contains(item.Title, query) || Contains(item.Summary, query)
        || Contains(item.Status, query) || Contains(item.Phase, query)
        || item.SourceTaskKeys.Any(key => KeyContains(key, query))
        || item.RelatedTaskKeys.Any(key => KeyContains(key, query));

    private List<GlobalSearchItem> SearchWiki(
        string query, int limit, IReadOnlyDictionary<string, string> colors, IDictionary<string, string> errors)
    {
        if (wikiSearch == null) return [];
        try
        {
            var matches = colors.Keys.SelectMany(project =>
                (wikiSearch.SearchTitlesAndHeadings(project, query, limit) ?? [])
                .Select(item => (Project: project, Item: item)));
            return matches
                .OrderBy(match => string.Equals(match.Item.Title, query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(match => match.Item.Title.Length)
                .ThenBy(match => match.Item.RelPath, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(match => new GlobalSearchItem(
                    "wiki", match.Project, colors.GetValueOrDefault(match.Project, DefaultColor),
                    match.Item.Title, match.Item.MatchingHeadingOrPath, Path: match.Item.RelPath,
                    IsWiki: true, UpdatedAt: match.Item.UpdatedAt, ProjectId: ProjectId(match.Project)))
                .ToList();
        }
        catch (Exception ex)
        {
            errors["wiki"] = "Some results could not be loaded.";
            logger.LogWarning(ex, "global-search-domain-failed domain={Domain}", "wiki");
            return [];
        }
    }

    private GlobalSearchRepositoryResult SearchRepository(GlobalSearchTarget target, string query, ISet<string> domains)
    {
        List<GlobalSearchItem> commits = [];
        List<GlobalSearchItem> files = [];
        long commitsMs = 0;
        long filesMs = 0;
        var commitsFromCache = true;
        var filesFromCache = true;
        var failed = new List<string>();

        if (domains.Contains("commits"))
        {
            var domainTimer = Stopwatch.StartNew();
            try
            {
                var (indexed, fromCache) = indexes.CommitIndex(target.Root);
                commitsFromCache = fromCache;
                commits = RankItems(MatchCommits(indexed, target, query), query).Take(MaxPerDomain).ToList();
            }
            catch (Exception ex)
            {
                failed.Add("commits");
                commitsFromCache = false;
                logger.LogWarning(ex, "global-search-domain-failed domain={Domain} project={Project}", "commits", target.Name);
            }
            commitsMs = domainTimer.ElapsedMilliseconds;
        }

        if (domains.Contains("files"))
        {
            var domainTimer = Stopwatch.StartNew();
            try
            {
                var (indexed, fromCache) = indexes.FileIndex(target.Root);
                filesFromCache = fromCache;
                files = RankItems(MatchFiles(indexed, target, query), query).Take(MaxPerDomain).ToList();
            }
            catch (Exception ex)
            {
                failed.Add("files");
                filesFromCache = false;
                logger.LogWarning(ex, "global-search-domain-failed domain={Domain} project={Project}", "files", target.Name);
            }
            filesMs = domainTimer.ElapsedMilliseconds;
        }

        return new GlobalSearchRepositoryResult(
            target, commits, files, commitsMs, filesMs, commitsFromCache, filesFromCache, failed);
    }

    internal static IEnumerable<GlobalSearchItem> MatchCommits(
        IEnumerable<IndexedCommit> commits, GlobalSearchTarget target, string query) => commits
        .Where(c => Contains(c.Sha, query) || Contains(c.ShortSha, query) || Contains(c.Subject, query))
        .Select(c => new GlobalSearchItem("commits", target.Name, target.Color, c.Subject, c.ShortSha, Sha: c.Sha));

    internal static IEnumerable<GlobalSearchItem> MatchFiles(
        IEnumerable<string> paths, GlobalSearchTarget target, string query) => paths
        .Where(path => Contains(path, query))
        .Select(path => new GlobalSearchItem("files", target.Name, target.Color, Path.GetFileName(path), path,
            Path: path, IsWiki: path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)
                // docs/app/ is a code contract, not a wiki page: never route it into the wiki viewer.
                && !path.StartsWith("docs/app/", StringComparison.OrdinalIgnoreCase)));

    internal static IEnumerable<GlobalSearchItem> RankItems(IEnumerable<GlobalSearchItem> items, string query) => items
        .OrderBy(i => string.Equals(i.Title, query, StringComparison.OrdinalIgnoreCase) || string.Equals(i.Subtitle, query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(i => i.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(i => i.Title.Length);

    private Dictionary<string, string> ResolveColors()
    {
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in registry.List().Where(p => !p.Archived))
            colors[project.DisplayName] = project.Color ?? DefaultColor;
        foreach (var watchPath in scanner.GetWatchPaths()) colors.TryAdd(watchPath.Name, DefaultColor);
        return colors;
    }

    private List<GlobalSearchTarget> ResolveTargets(IReadOnlyDictionary<string, string> colors) => registry.List()
        .Where(p => !p.Archived)
        .Select(p => (Name: p.DisplayName, Root: p.RepositoryPath ?? p.RootPath ?? ""))
        .Concat(scanner.GetWatchPaths()
            .Select(p => (p.Name, Root: p.RepositoryPath.Length > 0 ? p.RepositoryPath : p.RootPath)))
        .Where(p => !string.IsNullOrWhiteSpace(p.Root) && Directory.Exists(p.Root))
        .GroupBy(p => Path.GetFullPath(p.Root), StringComparer.OrdinalIgnoreCase)
        .Select(g => g.First())
        .Select(p => new GlobalSearchTarget(p.Name, p.Root, colors.GetValueOrDefault(p.Name, DefaultColor)))
        .ToList();

    private void RecordCompletion(
        string query, ISet<string> domains, int taskCount, int dossierCount, int wikiCount,
        IReadOnlyList<GlobalSearchRepositoryResult> results,
        long tasksMs, long dossiersMs, long wikiMs, long repositoriesMs, long durationMs)
    {
        // Per-repository cache attribution: without it a slow search is
        // indistinguishable from a search that simply had cold corpora.
        var cache = string.Join(' ', results.Select(r =>
            $"{r.Target.Name}={(r.CommitsFromCache ? "hit" : "miss")}/{(r.FilesFromCache ? "hit" : "miss")}:{r.DurationMs}ms"));
        logger.LogInformation(
            "global-search-completed queryLength={QueryLength} domains={Domains} tasks={Tasks} dossiers={Dossiers} wiki={Wiki} commits={Commits} files={Files} errors={Errors} repositories={Repositories} tasksMs={TasksMs} dossiersMs={DossiersMs} wikiMs={WikiMs} commitsMs={CommitsMs} filesMs={FilesMs} repositoriesMs={RepositoriesMs} cache={Cache} durationMs={DurationMs}",
            query.Length, string.Join(',', domains), taskCount, dossierCount, wikiCount,
            results.Sum(r => r.Commits.Count), results.Sum(r => r.Files.Count),
            results.Sum(r => r.FailedDomains.Count), results.Count,
            tasksMs, dossiersMs, wikiMs, results.Sum(r => r.CommitsMs), results.Sum(r => r.FilesMs), repositoriesMs, cache, durationMs);

        if (durationMs < SlowSearchWarnMs) return;
        var slowest = results.OrderByDescending(r => r.DurationMs).FirstOrDefault();
        logger.LogWarning(
            "global-search-slow durationMs={DurationMs} slowestRepository={Repository} slowestMs={SlowestMs} fromCache={FromCache}",
            durationMs, slowest?.Target.Name ?? "(none)", slowest?.DurationMs ?? 0,
            slowest is { CommitsFromCache: true, FilesFromCache: true });
    }

    private static bool Contains(string? value, string query) => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
    private static bool KeyContains(string? value, string query)
    {
        var normalized = NormalizeKey(query);
        return normalized.Length > 0 && NormalizeKey(value).Contains(normalized, StringComparison.Ordinal);
    }
    private static bool KeyEquals(string? value, string query)
    {
        var normalized = NormalizeKey(query);
        return normalized.Length > 0 && NormalizeKey(value) == normalized;
    }
    private static string NormalizeKey(string? value) =>
        string.Concat((value ?? "").Where(char.IsLetterOrDigit)).ToUpperInvariant();
    private string? ProjectId(string projectName) => registry.FindByIdOrDisplayName(projectName)?.Id;
    private static string? FirstMatchingLine(string text, string query) => text.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => Contains(x, query));
}
