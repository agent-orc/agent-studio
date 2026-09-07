using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using AgentStudio.Git;
using AgentStudio.Registry;
using AgentStudio.Shared;

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

/// <summary>Repository count and names, emitted once so the palette can render "i of n".</summary>
public sealed record GlobalSearchStart(string Query, int Repositories, IReadOnlyList<string> Domains);

/// <summary>One domain's results from one repository, or the whole task domain.</summary>
public sealed record GlobalSearchChunk(
    string Domain,
    string ProjectName,
    IReadOnlyList<GlobalSearchItem> Items,
    long DurationMs,
    bool CacheHit);

/// <summary>How far the repository sweep has got, per domain.</summary>
public sealed record GlobalSearchProgress(string Domain, int Completed, int Total);

/// <summary>A degraded domain. The rest of the search continues.</summary>
public sealed record GlobalSearchFailure(string Domain, string? ProjectName, string Message);

/// <summary>Terminal frame: total and per-domain durations.</summary>
public sealed record GlobalSearchSummary(
    long DurationMs,
    IReadOnlyDictionary<string, long> DomainDurationMs,
    int Repositories);

/// <summary>
/// Bounded, read-only workspace search over in-memory indexes.
///
/// <para><b>Index contract.</b> No domain spawns a process or reads a file on
/// the request path for a query it has already warmed. Tasks come from
/// <see cref="TaskSearchIndex"/>. Files and commits come from per-repository
/// snapshots memoized in <see cref="GitService"/>'s HEAD-keyed LRU under a key
/// that carries the repository and domain but <b>not the query</b>: typing a
/// second term against an unchanged HEAD spawns no git process. Matching then
/// happens in memory.</para>
///
/// <para><b>Delivery.</b> <see cref="StreamAsync"/> emits the task domain first
/// and then one chunk per repository as it completes, so the response is never
/// blocked by the slowest repository. <see cref="Search"/> keeps the original
/// single-shot contract for callers that want one JSON body.</para>
/// </summary>
public sealed class GlobalSearchService(
    TaskSearchIndex taskIndex,
    TaskScannerService scanner,
    GitService git,
    ProjectRegistry registry,
    ILogger<GlobalSearchService> logger)
{
    private const int MaxPerDomain = 30;

    /// <summary>
    /// Upper bound on the commit window held per repository. A palette match
    /// deeper than this is not worth a multi-second `git log` on a repository
    /// with a long history; see the common-problems entry for the mitigation.
    /// </summary>
    internal const int CommitWindow = 2_000;

    private const string DegradedMessage = "Some results could not be loaded.";

    /// <summary>
    /// Repository sweeps are I/O bound but each one forks a git process, so the
    /// fan-out is bounded rather than "all fifteen at once".
    /// </summary>
    private static int Concurrency => Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

    // ---------------------------------------------------------------- single shot

    public GlobalSearchResponse Search(string query, ISet<string> domains, int limit)
    {
        var timer = Stopwatch.StartNew();
        limit = Math.Clamp(limit, 1, MaxPerDomain);
        var colors = BuildProjectColors();
        var targets = ResolveRepositories(colors);
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var durations = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        var tasks = new List<GlobalSearchItem>();
        if (domains.Contains("tasks"))
        {
            var taskTimer = Stopwatch.StartNew();
            try { tasks = SearchTasks(query, limit, colors); }
            catch (Exception ex) { Degrade("tasks", null, ex, errors); }
            durations["tasks"] = taskTimer.ElapsedMilliseconds;
        }

        var commits = new List<GlobalSearchItem>();
        var files = new List<GlobalSearchItem>();
        var telemetry = new ConcurrentBag<RepositoryDomainResult>();
        SweepRepositories(query, domains, limit, targets, result =>
        {
            telemetry.Add(result);
            if (result.Error != null)
            {
                lock (errors) errors[result.Domain] = DegradedMessage;
                return;
            }
            var sink = result.Domain == "commits" ? commits : files;
            lock (sink) sink.AddRange(result.Items);
        }, CancellationToken.None);

        foreach (var domain in new[] { "commits", "files" })
        {
            if (!domains.Contains(domain)) continue;
            durations[domain] = telemetry.Where(r => r.Domain == domain).Select(r => r.DurationMs).DefaultIfEmpty(0).Max();
        }

        timer.Stop();
        LogCompletion(query, domains, tasks.Count, commits.Count, files.Count, errors.Count, durations, telemetry, timer.ElapsedMilliseconds);
        return new(query,
            tasks.Take(limit).ToList(),
            RankItems(commits, query).Take(limit).ToList(),
            RankItems(files, query).Take(limit).ToList(),
            errors, timer.ElapsedMilliseconds);
    }

    // ------------------------------------------------------------------ streaming

    /// <summary>
    /// Runs the same search but hands every frame to <paramref name="emit"/> as
    /// soon as it exists: <c>start</c>, <c>tasks</c>, then interleaved
    /// <c>chunk</c> / <c>progress</c> / <c>error</c> frames per repository, then
    /// <c>done</c>. Cancelling <paramref name="ct"/> stops the sweep; already
    /// running git processes are bounded by their own timeout.
    /// </summary>
    public async Task StreamAsync(
        string query,
        ISet<string> domains,
        int limit,
        Func<string, object, CancellationToken, Task> emit,
        CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        limit = Math.Clamp(limit, 1, MaxPerDomain);
        var colors = BuildProjectColors();
        var gitDomains = domains.Where(d => d is "commits" or "files").ToList();
        var targets = gitDomains.Count > 0 ? ResolveRepositories(colors) : [];
        var durations = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var errorCount = 0;

        await emit("start", new GlobalSearchStart(query, targets.Count, domains.ToList()), ct);

        var taskCount = 0;
        if (domains.Contains("tasks"))
        {
            var taskTimer = Stopwatch.StartNew();
            try
            {
                var items = SearchTasks(query, limit, colors);
                taskCount = items.Count;
                durations["tasks"] = taskTimer.ElapsedMilliseconds;
                await emit("chunk", new GlobalSearchChunk("tasks", "", items, taskTimer.ElapsedMilliseconds, true), ct);
            }
            catch (Exception ex)
            {
                errorCount++;
                durations["tasks"] = taskTimer.ElapsedMilliseconds;
                logger.LogWarning(ex, "global-search-domain-failed domain=tasks project=");
                await emit("error", new GlobalSearchFailure("tasks", null, DegradedMessage), ct);
            }
        }

        var commitCount = 0;
        var fileCount = 0;
        var telemetry = new ConcurrentBag<RepositoryDomainResult>();

        if (targets.Count > 0)
        {
            // The sweep is synchronous, CPU/IO bound work on pool threads; the
            // channel keeps the SSE writer single threaded and ordered without
            // making the producers wait on the socket.
            var frames = Channel.CreateUnbounded<(string Name, object Payload)>(
                new UnboundedChannelOptions { SingleReader = true });
            var completed = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var producer = Task.Run(() =>
            {
                try
                {
                    SweepRepositories(query, domains, limit, targets, result =>
                    {
                        telemetry.Add(result);
                        if (result.Error != null)
                        {
                            frames.Writer.TryWrite(("error",
                                new GlobalSearchFailure(result.Domain, result.ProjectName, DegradedMessage)));
                        }
                        else if (result.Items.Count > 0)
                        {
                            frames.Writer.TryWrite(("chunk", new GlobalSearchChunk(
                                result.Domain, result.ProjectName, result.Items, result.DurationMs, result.CacheHit)));
                        }
                        var done = completed.AddOrUpdate(result.Domain, 1, (_, current) => current + 1);
                        frames.Writer.TryWrite(("progress", new GlobalSearchProgress(result.Domain, done, targets.Count)));
                    }, ct);
                }
                finally
                {
                    frames.Writer.TryComplete();
                }
            }, CancellationToken.None);

            try
            {
                await foreach (var frame in frames.Reader.ReadAllAsync(ct))
                {
                    switch (frame.Payload)
                    {
                        case GlobalSearchChunk chunk when chunk.Domain == "commits": commitCount += chunk.Items.Count; break;
                        case GlobalSearchChunk chunk when chunk.Domain == "files": fileCount += chunk.Items.Count; break;
                        case GlobalSearchFailure: errorCount++; break;
                    }
                    await emit(frame.Name, frame.Payload, ct);
                }
            }
            finally
            {
                // Always observe the producer so a sweep fault is logged rather
                // than surfacing later as an unobserved task exception.
                await producer;
            }

            foreach (var domain in gitDomains)
                durations[domain] = telemetry.Where(r => r.Domain == domain).Select(r => r.DurationMs).DefaultIfEmpty(0).Max();
        }

        timer.Stop();
        await emit("done", new GlobalSearchSummary(timer.ElapsedMilliseconds, durations, targets.Count), ct);
        LogCompletion(query, domains, taskCount, commitCount, fileCount, errorCount, durations, telemetry, timer.ElapsedMilliseconds);
    }

    // -------------------------------------------------------------------- domains

    private List<GlobalSearchItem> SearchTasks(string query, int limit, IReadOnlyDictionary<string, string> colors)
    {
        var lowered = query.ToLowerInvariant();
        return taskIndex.GetEntries()
            .Where(entry => entry.Header.Contains(lowered, StringComparison.Ordinal)
                || entry.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => string.Equals(entry.Task.Key, query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(entry => entry.Task.LastActivity)
            .Take(limit)
            .Select(entry => new GlobalSearchItem("tasks", entry.Task.ProjectName,
                colors.GetValueOrDefault(entry.Task.ProjectName, "#6e6e6e"), entry.Task.Title,
                FirstMatchingLine(entry.Text, query) ?? entry.Task.State, entry.Task.TaskKey, entry.Task.State))
            .ToList();
    }

    /// <summary>
    /// Runs every requested git domain across every repository with a bounded
    /// fan-out, handing each per-repository result to <paramref name="onResult"/>
    /// the moment it is ready. Shared by the single-shot and streaming paths so
    /// both see identical caching and identical results.
    /// </summary>
    private void SweepRepositories(
        string query,
        ISet<string> domains,
        int limit,
        IReadOnlyList<RepositoryTarget> targets,
        Action<RepositoryDomainResult> onResult,
        CancellationToken ct)
    {
        var wanted = new[] { "commits", "files" }.Where(domains.Contains).ToList();
        if (wanted.Count == 0 || targets.Count == 0) return;

        Parallel.ForEach(
            targets,
            new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = ct },
            target =>
            {
                foreach (var domain in wanted)
                {
                    ct.ThrowIfCancellationRequested();
                    onResult(SearchRepository(domain, target, query, limit));
                }
            });
    }

    private RepositoryDomainResult SearchRepository(string domain, RepositoryTarget target, string query, int limit)
    {
        var timer = Stopwatch.StartNew();
        var cacheHit = true;
        try
        {
            var items = domain == "commits"
                ? MatchCommits(CommitIndex(target.Root, ref cacheHit), target, query)
                : MatchFiles(FileIndex(target.Root, ref cacheHit), target, query);
            return new(domain, target.Name, RankItems(items, query).Take(limit).ToList(),
                timer.ElapsedMilliseconds, cacheHit, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "global-search-domain-failed domain={Domain} project={Project}", domain, target.Name);
            return new(domain, target.Name, [], timer.ElapsedMilliseconds, cacheHit, ex.Message);
        }
    }

    // --------------------------------------------------------------------- indexes

    /// <summary>
    /// Tracked and untracked paths for one repository, memoized per HEAD. The
    /// key deliberately omits the query: the whole point of the index is that a
    /// new search term costs zero processes.
    /// </summary>
    private IReadOnlyList<string> FileIndex(string root, ref bool cacheHit)
    {
        var miss = false;
        var index = git.MemoizeByHead(root, $"global-search-file-index|{root}", () =>
        {
            miss = true;
            return ReadFilePaths(root);
        });
        cacheHit &= !miss;
        return index;
    }

    /// <summary>Bounded commit window for one repository, memoized per HEAD.</summary>
    private IReadOnlyList<CommitRecord> CommitIndex(string root, ref bool cacheHit)
    {
        var miss = false;
        var index = git.MemoizeByHead(root, $"global-search-commit-index|{root}", () =>
        {
            miss = true;
            return ReadCommits(root);
        });
        cacheHit &= !miss;
        return index;
    }

    internal static IReadOnlyList<string> ReadFilePaths(string root) =>
        RunGit(root, ["ls-files", "--cached", "--others", "--exclude-standard"])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.TrimEnd('\r').Replace('\\', '/'))
            .ToList();

    internal static IReadOnlyList<CommitRecord> ReadCommits(string root) =>
        RunGit(root, ["log", "--all", "--no-merges", $"--max-count={CommitWindow}", "--pretty=format:%H%x1f%h%x1f%s"])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\x1f'))
            .Where(parts => parts.Length >= 3)
            .Select(parts => new CommitRecord(parts[0], parts[1], parts[2]))
            .ToList();

    internal static List<GlobalSearchItem> MatchFiles(IEnumerable<string> paths, RepositoryTarget target, string query) =>
        paths.Where(path => Contains(path, query))
            .Select(path => new GlobalSearchItem("files", target.Name, target.Color, Path.GetFileName(path), path,
                Path: path, IsWiki: path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)
                    // docs/app/ is a code contract, not a wiki page: never route it into the wiki viewer.
                    && !path.StartsWith("docs/app/", StringComparison.OrdinalIgnoreCase)))
            .ToList();

    internal static List<GlobalSearchItem> MatchCommits(IEnumerable<CommitRecord> commits, RepositoryTarget target, string query) =>
        commits.Where(c => Contains(c.Sha, query) || Contains(c.ShortSha, query) || Contains(c.Subject, query))
            .Select(c => new GlobalSearchItem("commits", target.Name, target.Color, c.Subject, c.ShortSha, Sha: c.Sha))
            .ToList();

    // ------------------------------------------------------------------ discovery

    private Dictionary<string, string> BuildProjectColors()
    {
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in registry.List().Where(p => !p.Archived))
            colors[project.DisplayName] = project.Color ?? "#6e6e6e";
        foreach (var watchPath in scanner.GetWatchPaths()) colors.TryAdd(watchPath.Name, "#6e6e6e");
        return colors;
    }

    private List<RepositoryTarget> ResolveRepositories(IReadOnlyDictionary<string, string> colors) =>
        registry.List().Where(p => !p.Archived)
            .Select(p => (Name: p.DisplayName, Root: p.RepositoryPath ?? p.RootPath ?? ""))
            .Concat(scanner.GetWatchPaths().Select(p => (p.Name, Root: p.RepositoryPath.Length > 0 ? p.RepositoryPath : p.RootPath)))
            .Where(p => !string.IsNullOrWhiteSpace(p.Root) && Directory.Exists(p.Root))
            .GroupBy(p => Path.GetFullPath(p.Root), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(p => new RepositoryTarget(p.Name, p.Root, colors.GetValueOrDefault(p.Name, "#6e6e6e")))
            .ToList();

    // --------------------------------------------------------------- observability

    private void LogCompletion(
        string query,
        ISet<string> domains,
        int tasks,
        int commits,
        int files,
        int errors,
        IReadOnlyDictionary<string, long> durations,
        IReadOnlyCollection<RepositoryDomainResult> telemetry,
        long elapsedMs)
    {
        var cacheReport = telemetry.Count == 0
            ? ""
            : string.Join(',', telemetry
                .GroupBy(r => r.ProjectName, StringComparer.Ordinal)
                .Select(g => $"{g.Key}={(g.All(r => r.CacheHit) ? "hit" : "miss")}"));

        logger.LogInformation(
            "global-search-completed queryLength={QueryLength} domains={Domains} tasks={Tasks} commits={Commits} files={Files} errors={Errors} tasksMs={TasksMs} commitsMs={CommitsMs} filesMs={FilesMs} repositoryCache={RepositoryCache} durationMs={DurationMs}",
            query.Length, string.Join(',', domains), tasks, commits, files, errors,
            durations.GetValueOrDefault("tasks"), durations.GetValueOrDefault("commits"), durations.GetValueOrDefault("files"),
            cacheReport, elapsedMs);

        if (elapsedMs <= 5_000) return;
        var slowest = telemetry.OrderByDescending(r => r.DurationMs).FirstOrDefault();
        logger.LogWarning(
            "global-search-slow durationMs={DurationMs} slowestProject={Project} slowestDomain={Domain} slowestMs={SlowestMs} cacheHit={CacheHit}",
            elapsedMs, slowest?.ProjectName ?? "(none)", slowest?.Domain ?? "(none)", slowest?.DurationMs ?? 0, slowest?.CacheHit ?? false);
    }

    internal static IEnumerable<GlobalSearchItem> RankItems(IEnumerable<GlobalSearchItem> items, string query) => items
        .OrderBy(i => string.Equals(i.Title, query, StringComparison.OrdinalIgnoreCase) || string.Equals(i.Subtitle, query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(i => i.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(i => i.Title.Length);

    private void Degrade(string domain, string? project, Exception ex, IDictionary<string, string> errors)
    {
        errors[domain] = DegradedMessage;
        logger.LogWarning(ex, "global-search-domain-failed domain={Domain} project={Project}", domain, project);
    }

    private static bool Contains(string? value, string query) => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
    private static string? FirstMatchingLine(string text, string query) => text.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => Contains(x, query));

    /// <summary>
    /// Process-wide count of git spawns made by search index builds. The
    /// no-respawn contract ("a new query against an unchanged HEAD costs zero
    /// processes") is the whole point of the HEAD-only cache keys, so it is
    /// asserted directly rather than inferred from a stopwatch.
    /// </summary>
    internal static long GitProcessSpawns;

    private static string RunGit(string root, IReadOnlyList<string> args)
    {
        Interlocked.Increment(ref GitProcessSpawns);
        using var process = new Process { StartInfo = new ProcessStartInfo("git") {
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        }};
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(true);
            throw new TimeoutException("git search exceeded 10 seconds");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException(stderr.Trim());
        return stdout;
    }

    internal sealed record CommitRecord(string Sha, string ShortSha, string Subject);

    internal sealed record RepositoryTarget(string Name, string Root, string Color);

    private sealed record RepositoryDomainResult(
        string Domain,
        string ProjectName,
        IReadOnlyList<GlobalSearchItem> Items,
        long DurationMs,
        bool CacheHit,
        string? Error);
}
