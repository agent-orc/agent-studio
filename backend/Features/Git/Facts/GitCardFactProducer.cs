using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Git;

/// <summary>Bounds of one producer pass of Git facts S3.</summary>
public sealed record GitCardFactOptions
{
    /// <summary>Git work one pass may start before it hands the rest to a follow-up pass.</summary>
    public TimeSpan WorkBudget { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Range and window reads (one process each) per pass.</summary>
    public int MaxRangeReadsPerPass { get; init; } = 64;

    /// <summary>Commits and changed lines per batched patch read.</summary>
    public int MaxPatchCommitsPerRead { get; init; } = 40;
    public int MaxPatchLinesPerRead { get; init; } = 60_000;

    /// <summary>A commit above this many changed lines or files gets a manifest-only object.</summary>
    public int MaxLinesPerObject { get; init; } = 200_000;

    /// <summary>Legacy attribution backfills per pass.</summary>
    public int MaxBackfillsPerPass { get; init; } = 5;

    public static GitCardFactOptions FromConfiguration(IConfiguration config) => new()
    {
        WorkBudget = TimeSpan.FromSeconds(Math.Clamp(config.GetValue<int?>("GitFacts:WorkBudgetSeconds") ?? 20, 1, 120)),
        MaxRangeReadsPerPass = Math.Clamp(config.GetValue<int?>("GitFacts:MaxRangeReadsPerPass") ?? 64, 1, 1_000),
    };
}

/// <summary>
/// What one pass computed before the generation number is known. The index
/// commits <see cref="Write"/> inside the generation's publish transaction,
/// then publishes <see cref="Pointer"/> with that number.
/// </summary>
internal sealed class GitCardFactPass
{
    internal GitCardFactPass(string project, GitCardFactsWrite write, Func<long, DateTimeOffset, string, GitPublishedFacts> pointer,
        bool spawned, bool backlog)
    {
        Project = project;
        Write = write;
        _pointer = pointer;
        Spawned = spawned;
        Backlog = backlog;
    }

    private readonly Func<long, DateTimeOffset, string, GitPublishedFacts> _pointer;
    internal string Project { get; }
    internal GitCardFactsWrite Write { get; }

    /// <summary>True when the pass started at least one Git process.</summary>
    internal bool Spawned { get; }

    /// <summary>Work left for a follow-up pass (budget reached or a read cut short).</summary>
    internal bool Backlog { get; }

    internal GitPublishedFacts Pointer(long generation, DateTimeOffset publishedAt, string state)
        => _pointer(generation, publishedAt, state);
}

/// <summary>
/// The Git facts S3 producer (docs/git-facts-architecture, sections 03 and 04):
/// commit facts, reachability, run trailer and commit range indexes, curated
/// merges, diff objects and the project integration view, computed inside the
/// <see cref="GitStateIndexService"/> run of each repository and published with
/// its generation. It is the only Git client of the card read paths, which
/// join what it publishes through <see cref="GitCardFactReader"/>.
///
/// <list type="bullet">
/// <item>Commit facts for new SHAs come from one batched <c>git log --no-walk</c>.</item>
/// <item>Reachability per target ref is incremental (<see cref="GitReachabilityIndex"/>).</item>
/// <item>The run trailer index replaces <c>log --all --grep</c> on read paths; it
/// scans only commits new since the tips it covered.</item>
/// <item>Diff objects for the default diff of every live card are written at
/// publication; a read that misses one queues priority work.</item>
/// <item>The integration view joins stored facts with tasks; a task-only change
/// rejoins without Git.</item>
/// </list>
/// </summary>
internal sealed class GitCardFactProducer
{
    private const string TrailerCursor = "run-trailer-tips";
    private const string BranchCursor = "integration-branch";
    private const string PromotionCursor = "promotion";
    private const string MergeCursorPrefix = "merges:";
    private const string ReleaseBranch = TaskProvenanceService.ReleaseBranch;

    /// <summary>Lanes whose cards get their default diff objects at publication.</summary>
    private static readonly HashSet<string> LiveLanes = new(StringComparer.Ordinal)
    {
        TaskStates.Progress, TaskStates.AutoReview, TaskStates.HumanReview, TaskStates.Escalated, TaskStates.Completed,
    };

    private readonly GitReachabilityIndex _reachability;
    private readonly GitCardFactReader _reader;
    private readonly GitFactStore? _store;
    private readonly Func<string, string> _configuredBranch;
    private readonly Func<string, string?> _resolveRoot;
    private readonly Func<string, string?, string> _resolveIntegrationBranch;
    private readonly Func<TaskInfo, bool>? _backfill;
    private readonly GitCardFactOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly TaskFileHistoryService? _files;
    private readonly ConcurrentDictionary<string, RepoFacts> _repos = new(StringComparer.OrdinalIgnoreCase);

    internal GitCardFactProducer(
        GitReachabilityIndex reachability,
        GitCardFactReader reader,
        GitFactStore? store,
        Func<string, string> configuredBranch,
        Func<string, string?> resolveRoot,
        Func<string, string?, string> resolveIntegrationBranch,
        Func<TaskInfo, bool>? backfill,
        GitCardFactOptions options,
        ILogger logger,
        TimeProvider time,
        TaskFileHistoryService? files = null)
    {
        _reachability = reachability;
        _reader = reader;
        _store = store;
        _configuredBranch = configuredBranch;
        _resolveRoot = resolveRoot;
        _resolveIntegrationBranch = resolveIntegrationBranch;
        _backfill = backfill;
        _options = options;
        _logger = logger;
        _time = time;
        _files = files;
    }

    /// <summary>
    /// Registers a repository with the reader and enables commit-graph
    /// maintenance on its checkout. <paramref name="requestPass"/> asks the
    /// index for a run when a read queues priority work.
    /// </summary>
    internal void Register(string project, string watchPath, string repositoryPath, Action requestPass)
    {
        var repo = new RepoFacts(project, watchPath, repositoryPath);
        _repos[project] = repo;
        _reachability.Track(repositoryPath);
        _reader.Register(watchPath, project, work =>
        {
            if (repo.Enqueue(work)) requestPass();
        });
        EnableCommitGraph(repositoryPath);
    }

    internal void Forget(string project)
    {
        if (!_repos.TryRemove(project, out var repo)) return;
        _reader.Unregister(repo.WatchPath);
        _reachability.Forget(repo.Root);
    }

    /// <summary>True when queued, failed or budget-limited work waits for a pass.</summary>
    internal bool HasPendingWork(string project)
        => _repos.TryGetValue(project, out var repo) && (repo.QueueLength > 0 || repo.NeedsPass);

    /// <summary>
    /// Cold start: the stored S3 facts of the restored generation, served as
    /// <c>restored</c> before any Git runs.
    /// </summary>
    internal void Restore(string project, long generation, DateTimeOffset publishedAt,
        IReadOnlyDictionary<string, string> refs)
    {
        if (!_repos.TryGetValue(project, out var repo) || _store is not { IsAvailable: true }) return;
        var stored = _store.LoadCardFacts(project);
        if (stored is null) return;
        foreach (var commit in stored.Commits) repo.Catalog.AddCommit(commit);
        foreach (var (key, shas) in stored.Ranges) repo.Catalog.AddRange(key, shas);
        foreach (var (job, shas) in stored.RunCommits) repo.RunCommits[job] = shas.ToList();
        foreach (var (name, merges) in stored.Merges)
            repo.Merges[name] = new MergeIndex(stored.Cursors.GetValueOrDefault(MergeCursorPrefix + name), merges.ToList());
        if (stored.Cursors.TryGetValue(TrailerCursor, out var tips))
            repo.TrailerTips = JsonSerializer.Deserialize<string[]>(tips)?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (stored.Cursors.TryGetValue(PromotionCursor, out var promotion))
            repo.Promotion = JsonSerializer.Deserialize<PromotionFacts>(promotion);
        repo.IntegrationBranch = stored.Cursors.GetValueOrDefault(BranchCursor);
        repo.View = stored.ProjectView;
        repo.TaskGeneration = stored.ProjectView?.TaskGeneration ?? 0;
        repo.ViewSignature = stored.ProjectView?.TaskSignature;
        _reachability.Seed(repo.Root, stored.Reachability);
        repo.LastRefs = refs;
        if (repo.IntegrationBranch is null || stored.Commits.Count == 0 && stored.Reachability.Count == 0)
            repo.NeedsPass = true;
        var reach = new Dictionary<string, GitReachableSet>(StringComparer.Ordinal);
        var complete = true;
        foreach (var fullRef in TrackedRefs(repo.IntegrationBranch ?? _configuredBranch(project)))
        {
            var exists = refs.TryGetValue(fullRef, out var tip) && GitReachabilityIndex.IsSha(tip);
            if (_reachability.Peek(repo.Root, fullRef) is { } set)
            {
                reach[fullRef] = set;
                // A set for another tip than the generation's still serves (an
                // older generation's commits stay reachable after a fast-forward)
                // until the next pass brings it up to date.
                if (exists && !string.Equals(tip, set.Tip, StringComparison.OrdinalIgnoreCase)) repo.NeedsPass = true;
            }
            else if (exists) complete = false;
        }
        repo.Generation = generation;
        if (!complete)
        {
            // Without a target ref's set, membership would read "not contained":
            // reads stay warming until the first pass computes it.
            repo.NeedsPass = true;
            _logger.LogInformation("git-card-facts-restore-incomplete repository={Repository}", project);
            return;
        }
        var pointer = Pointer(repo, refs, repo.IntegrationBranch ?? _configuredBranch(project), reach,
            generation, publishedAt, GitPublishedFacts.RestoredState);
        repo.Published = pointer;
        _reader.Publish(repo.WatchPath, pointer);
        _logger.LogInformation(
            "git-card-facts-restored repository={Repository} commits={Commits} refs={Refs} jobs={Jobs} view={View}",
            project, stored.Commits.Count, stored.Reachability.Count, stored.RunCommits.Count, stored.ProjectView is not null);
    }

    /// <summary>The restored generation was confirmed from files: serve it as ready.</summary>
    internal void Confirm(string project, DateTimeOffset at)
    {
        if (!_repos.TryGetValue(project, out var repo) || repo.Published is not { } published) return;
        var ready = published with { State = GitPublishedFacts.ReadyState, PublishedAt = at };
        repo.Published = ready;
        _reader.Publish(repo.WatchPath, ready);
    }

    /// <summary>Publishes a pass's pointer; the index calls it with the generation's projection swap.</summary>
    internal void Publish(GitCardFactPass pass, long generation, DateTimeOffset publishedAt)
    {
        if (!_repos.TryGetValue(pass.Project, out var repo)) return;
        repo.Generation = generation;
        if (pass.Write.ProjectView is { } view && ReferenceEquals(view, repo.View))
            repo.View = view with { Generation = generation };
        var pointer = pass.Pointer(generation, publishedAt, GitPublishedFacts.ReadyState) with { ProjectView = repo.View };
        repo.Published = pointer;
        _reader.Publish(repo.WatchPath, pointer);
    }

    /// <summary>Writes a pass that has no new generation (an unchanged-input run) with the current one.</summary>
    internal void WriteWithoutGeneration(GitCardFactPass pass)
    {
        if (pass.Write.IsEmpty || _store is not { IsAvailable: true }) return;
        if (!_repos.TryGetValue(pass.Project, out var repo)) return;
        try { _store.WriteCardFacts(pass.Write, repo.Generation); }
        catch (Exception ex) when (ex is DbException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "git-card-facts-write-failed repository={Repository}", pass.Project);
        }
    }

    /// <summary>
    /// One producer pass. Returns null when the repository is unknown or its
    /// Git reads failed as a whole: the last published facts keep serving and
    /// the next pass retries. A task-only change rejoins stored facts here
    /// without starting Git.
    /// </summary>
    internal GitCardFactPass? Prepare(string project, IReadOnlyList<TaskInfo> tasks, TaskListGitProjection projection,
        CancellationToken cancellationToken)
    {
        if (!_repos.TryGetValue(project, out var repo)) return null;
        var clock = Stopwatch.StartNew();
        var spawnsBefore = GitProcessTelemetry.CurrentTally()?.Spawns ?? 0;
        var work = repo.Drain();
        try
        {
            var pass = PrepareCore(repo, tasks, projection, work, clock, cancellationToken);
            repo.NeedsPass = repo.QueueLength > 0;
            return pass;
        }
        catch (Exception ex) when (ex is GitFactReadException or IOException or UnauthorizedAccessException
            or JsonException or DbException)
        {
            // Queued reads are kept for the retry; the last good facts serve.
            repo.Requeue(work);
            repo.NeedsPass = true;
            _logger.LogWarning(ex, "git-card-facts-pass-failed repository={Repository} spawns={Spawns}", project,
                (GitProcessTelemetry.CurrentTally()?.Spawns ?? 0) - spawnsBefore);
            return null;
        }
    }

    private GitCardFactPass PrepareCore(RepoFacts repo, IReadOnlyList<TaskInfo> tasks,
        TaskListGitProjection projection, IReadOnlyList<GitFactWork> work, Stopwatch clock,
        CancellationToken cancellationToken)
    {
        var spawnsBefore = GitProcessTelemetry.CurrentTally()?.Spawns ?? 0;
        var root = ResolveRoot(repo);
        var refs = GitRefObjectIds.Read(root);
        if (refs.ContainsKey(GitRefObjectIds.FormatKey) || refs.ContainsKey(GitRefObjectIds.TruncatedKey))
            throw new GitFactReadException("refs", "ref store not readable from files (reftable or truncated)");
        var refsChanged = repo.LastRefs is null || !SameRefs(repo.LastRefs, refs);
        if (refsChanged) repo.Catalog.ForgetMissing();
        var newCommits = new List<GitCommitFact>();
        var newRanges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var newRunCommits = new List<(string JobId, string Sha)>();
        var mergeChanges = new List<GitMergeIndexChange>();
        var cursors = new Dictionary<string, string>(StringComparer.Ordinal);
        var deferred = new List<GitFactWork>();

        // 1. Integration branch and reachability of the target refs, shared
        //    with card classification through the index.
        var integrationBranch = ResolveIntegrationBranch(repo, root, refs);
        if (!string.Equals(integrationBranch, repo.IntegrationBranch, StringComparison.Ordinal))
        {
            repo.IntegrationBranch = integrationBranch;
            cursors[BranchCursor] = integrationBranch;
        }
        _reachability.Track(root);
        var reach = new Dictionary<string, GitReachableSet>(StringComparer.Ordinal);
        foreach (var fullRef in TrackedRefs(integrationBranch))
        {
            if (!refs.TryGetValue(fullRef, out var tip) || !GitReachabilityIndex.IsSha(tip)) continue;
            reach[fullRef] = _reachability.Ensure(root, fullRef, tip)
                ?? throw new GitFactReadException("rev-list", "reachability of " + fullRef);
        }

        // 2. Run trailer index: only commits new since the covered tips.
        if (refsChanged || repo.TrailerTips is null)
            ScanRunTrailers(repo, root, newRunCommits, cursors);

        // 3. Commit ranges: queued reads first, then the run ranges of live cards.
        var rangeWork = work.OfType<GitRangeWork>().Cast<GitFactWork>()
            .Concat(work.OfType<GitWindowWork>())
            .Concat(LiveRunRanges(repo, tasks))
            .DistinctBy(item => item.Key)
            .Where(item => item is GitWindowWork { ToUtc: null } || !repo.Catalog.HasRange(item.Key))
            .ToList();
        var rangeReads = 0;
        var openWindowShas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in rangeWork)
        {
            if (rangeReads >= _options.MaxRangeReadsPerPass || OverBudget(clock))
            {
                deferred.Add(item);
                continue;
            }
            rangeReads++;
            if (item is GitWindowWork { ToUtc: null } open)
            {
                // A run in flight: read to now, keep in memory until HEAD moves.
                var current = GitFactCommands.WindowCommits(root, open.FromUtc, _time.GetUtcNow().UtcDateTime) ?? [];
                repo.Catalog.AddOpenWindow(item.Key, refs.GetValueOrDefault("HEAD"), current);
                foreach (var sha in current) Need(repo, openWindowShas, sha);
                continue;
            }
            var shas = item switch
            {
                GitRangeWork range => GitFactCommands.RangeCommits(root, range.BaseSha, range.HeadSha),
                GitWindowWork window => GitFactCommands.WindowCommits(root, window.FromUtc, window.ToUtc!.Value),
                _ => null,
            } ?? [];
            repo.Catalog.AddRange(item.Key, shas);
            newRanges[item.Key] = shas;
        }

        // 4. Commit facts for every SHA a card, a range or the trailer index
        //    names and the store does not hold yet: one batched read.
        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            foreach (var commit in task.Commits) Need(repo, needed, commit.Sha);
            if (task.Commit is { } recorded) Need(repo, needed, recorded.Sha);
        }
        foreach (var shas in repo.RunCommits.Values) foreach (var sha in shas) Need(repo, needed, sha);
        foreach (var shas in newRanges.Values) foreach (var sha in shas) Need(repo, needed, sha);
        needed.UnionWith(openWindowShas);
        foreach (var item in work)
        {
            switch (item)
            {
                case GitCommitWork commit: Need(repo, needed, commit.Sha); break;
                case GitCommitDiffWork diff: Need(repo, needed, diff.Sha); break;
                case GitAggregateDiffWork aggregate: foreach (var sha in aggregate.Shas) Need(repo, needed, sha); break;
            }
        }
        if (needed.Count > 0)
        {
            var facts = GitFactCommands.ReadCommits(root, needed, out var missing);
            foreach (var fact in facts)
            {
                if (repo.Catalog.Commit(fact.Sha) is null) newCommits.Add(fact);
                repo.Catalog.AddCommit(fact);
            }
            foreach (var requested in needed)
            {
                var match = facts.FirstOrDefault(fact => fact.Sha.StartsWith(requested, StringComparison.OrdinalIgnoreCase));
                if (match is not null) repo.Catalog.AddAbbreviation(requested, match.Sha);
            }
            foreach (var sha in missing) repo.Catalog.MarkMissing(sha);
        }

        // 5. Curated merges per integration ref name, incremental per ref move.
        var mergeRefs = new HashSet<string>(StringComparer.Ordinal)
        {
            OriginRef(integrationBranch),
            TaskIntegrationBranch.Name(_configuredBranch(repo.Project)),
        };
        foreach (var item in work.OfType<GitMergesWork>()) mergeRefs.Add(item.RefName);
        foreach (var status in projection.Integration.Values)
            if (!string.IsNullOrWhiteSpace(status.IntegrationBranch)) mergeRefs.Add(status.IntegrationBranch);
        foreach (var name in mergeRefs.Where(name => !string.IsNullOrWhiteSpace(name)))
            IndexMerges(repo, root, refs, reach, name, mergeChanges, cursors);

        // 6. S4 file work from a card read has priority over default diffs.
        foreach (var fileWork in work.OfType<GitFileHistoryWork>())
        {
            if (_files is null || _store is null || OverBudget(clock))
            {
                deferred.Add(fileWork);
                continue;
            }
            var fact = _files.BuildIndexedHistory(fileWork, repo.Project);
            if (fact is null)
            {
                deferred.Add(fileWork);
                continue;
            }
            _store.WritePathHistory(fact);
        }

        // 7. Diff objects: queued first, then the default diff of live cards.
        var draft = Pointer(repo, refs, integrationBranch, reach, repo.Generation, _time.GetUtcNow(),
            GitPublishedFacts.ReadyState);
        var diffWork = work.Where(item => item is GitCommitDiffWork or GitAggregateDiffWork or GitRangeDiffWork)
            .Concat(LiveDefaultDiffs(repo, tasks, draft))
            .DistinctBy(item => item.Key)
            .ToList();
        deferred.AddRange(WriteDiffObjects(repo, root, diffWork, clock));

        // 8. Legacy attribution backfill, moved off GET /commits.
        Backfill(repo, tasks, clock);

        // 9. The project integration view from the same generation's facts.
        var view = ComposeView(repo, root, tasks, projection, refs, integrationBranch, reach, cursors);

        foreach (var item in deferred) repo.Enqueue([item]);
        repo.LastRefs = refs;
        var write = new GitCardFactsWrite(repo.Project, newCommits, _reachability.TakeChanges(root), newRanges,
            newRunCommits, mergeChanges, cursors, view);
        var spawned = (GitProcessTelemetry.CurrentTally()?.Spawns ?? 0) > spawnsBefore;
        if (spawned || write.ProjectView is not null)
        {
            _logger.LogInformation(
                "git-card-facts-pass repository={Repository} commits={Commits} ranges={Ranges} trailers={Trailers} merges={Merges} view={View} deferred={Deferred} ms={Ms}",
                repo.Project, newCommits.Count, newRanges.Count, newRunCommits.Count, mergeChanges.Count,
                view is not null, deferred.Count, clock.ElapsedMilliseconds);
        }
        var runCommits = repo.RunCommits.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToArray(),
            StringComparer.Ordinal);
        var merges = repo.Merges.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<GitIntegrationMergeCommit>)pair.Value.Merges.ToArray(), StringComparer.Ordinal);
        return new GitCardFactPass(repo.Project, write,
            (generation, publishedAt, state) => new GitPublishedFacts(repo.Project, repo.WatchPath, root, generation,
                publishedAt, state, refs, integrationBranch, ReleaseBranch, reach, runCommits, merges, repo.View,
                repo.Catalog),
            // A backlog asks for an immediate follow-up pass only after a pass
            // that made Git progress; otherwise the next trigger retries it.
            spawned, deferred.Count > 0 && spawned);
    }

    private void ScanRunTrailers(RepoFacts repo, string root, List<(string JobId, string Sha)> found,
        Dictionary<string, string> cursors)
    {
        var tips = GitRefObjectIds.ReadAll(root).Values.Where(GitReachabilityIndex.IsSha)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var covered = repo.TrailerTips ?? [];
        var fresh = tips.Where(tip => !covered.Contains(tip)).ToList();
        if (fresh.Count > 0)
        {
            // A covered tip that is gone as an object fails the scan; then only
            // tips still referenced now bound it, and finally nothing does.
            var scan = GitFactCommands.ScanRunTrailers(root, fresh, covered)
                ?? GitFactCommands.ScanRunTrailers(root, fresh, covered.Where(tips.Contains).ToList())
                ?? GitFactCommands.ScanRunTrailers(root, fresh, [])
                ?? throw new GitFactReadException("log", "run trailer scan failed");
            foreach (var (sha, jobId) in scan)
            {
                if (!repo.RunCommits.TryGetValue(jobId, out var shas)) repo.RunCommits[jobId] = shas = [];
                if (shas.Contains(sha, StringComparer.OrdinalIgnoreCase)) continue;
                shas.Add(sha);
                found.Add((jobId, sha));
            }
        }
        if (repo.TrailerTips is null || !repo.TrailerTips.SetEquals(tips))
        {
            repo.TrailerTips = tips;
            cursors[TrailerCursor] = JsonSerializer.Serialize(tips.Order(StringComparer.Ordinal).ToArray());
        }
    }

    /// <summary>The SHA ranges of live cards' runs, from session events cached by file stamp.</summary>
    private static IEnumerable<GitFactWork> LiveRunRanges(RepoFacts repo, IReadOnlyList<TaskInfo> tasks)
    {
        foreach (var task in tasks)
        {
            if (!LiveLanes.Contains(task.State) || string.IsNullOrWhiteSpace(task.FolderPath)) continue;
            foreach (var run in repo.Runs(task))
            {
                if (GitCardFactJoins.IsSha(run.HeadShaBefore) && GitCardFactJoins.IsSha(run.HeadShaAfter)
                    && !string.Equals(run.HeadShaBefore, run.HeadShaAfter, StringComparison.OrdinalIgnoreCase))
                    yield return new GitRangeWork(run.HeadShaBefore!, run.HeadShaAfter!);
            }
        }
    }

    /// <summary>The default-diff objects of every live card, as the Git pane selects them.</summary>
    private static IEnumerable<GitFactWork> LiveDefaultDiffs(RepoFacts repo, IReadOnlyList<TaskInfo> tasks,
        GitPublishedFacts draft)
    {
        foreach (var task in tasks)
        {
            if (!LiveLanes.Contains(task.State) || task.Commits.Count == 0 && task.Commit is null) continue;
            var misses = new List<GitFactWork>();
            var aggregate = GitCardFactJoins.Commits(task, repo.Runs(task), draft, misses);
            // A card whose facts are still missing gets its objects on read.
            if (misses.Count > 0) continue;
            foreach (var item in GitCardFactJoins.DefaultDiffWork(task, GitCardFactJoins.ActiveShas(task, aggregate)))
                yield return item;
        }
    }

    /// <summary>Writes missing diff objects; returns the work left for a later pass.</summary>
    private List<GitFactWork> WriteDiffObjects(RepoFacts repo, string root, IReadOnlyList<GitFactWork> work, Stopwatch clock)
    {
        var deferred = new List<GitFactWork>();
        var now = _time.GetUtcNow();
        PrimeManifests(repo, work.SelectMany(item => item switch
        {
            GitCommitDiffWork commit when repo.Catalog.Commit(commit.Sha) is { } fact
                => [GitDiffObjectKey.Commit(repo.Project, fact).ObjectId],
            GitAggregateDiffWork aggregate when Members(repo, aggregate) is { Count: > 0 } members
                => members.Select(fact => GitDiffObjectKey.Commit(repo.Project, fact).ObjectId)
                    .Append(GitDiffObjectKey.Aggregate(repo.Project, members.Select(fact => fact.Sha).ToList()).ObjectId),
            GitRangeDiffWork range => [GitDiffObjectKey.Range(repo.Project, range.BaseSha, range.HeadSha).ObjectId],
            _ => (IEnumerable<string>)[],
        }));
        var commitObjects = new Dictionary<string, GitCommitFact>(StringComparer.OrdinalIgnoreCase);
        var aggregates = new List<IReadOnlyList<GitCommitFact>>();
        foreach (var item in work)
        {
            switch (item)
            {
                case GitCommitDiffWork commit when repo.Catalog.Commit(commit.Sha) is { } fact:
                    commitObjects[fact.Sha] = fact;
                    break;
                case GitAggregateDiffWork aggregate:
                    var members = Members(repo, aggregate);
                    if (members is null) deferred.Add(item);
                    if (members is not { Count: > 0 }) break;
                    aggregates.Add(members);
                    foreach (var member in members) commitObjects[member.Sha] = member;
                    break;
                case GitRangeDiffWork range:
                    var key = GitDiffObjectKey.Range(repo.Project, range.BaseSha, range.HeadSha);
                    if (Exists(repo, key.ObjectId)) break;
                    if (OverBudget(clock)) { deferred.Add(item); break; }
                    var files = GitFactCommands.RangeFiles(root, range.BaseSha, range.HeadSha);
                    var lines = files.Sum(file => file.Added + file.Removed);
                    var patch = lines > _options.MaxLinesPerObject || files.Count > GitDiffObjectManifest.MaxFiles
                        ? null : GitFactCommands.RangePatch(root, range.BaseSha, range.HeadSha);
                    _reader.Store(repo.Catalog, patch is null
                        ? GitDiffObjectBuilder.TooLarge(key, files, now)
                        : GitDiffObjectBuilder.FromRange(repo.Project, range.BaseSha, range.HeadSha, files, patch, now));
                    break;
            }
        }

        var pending = commitObjects.Values
            .Where(fact => !Exists(repo, GitDiffObjectKey.Commit(repo.Project, fact).ObjectId))
            .ToList();
        var batches = new List<List<GitCommitFact>>();
        var batch = new List<GitCommitFact>();
        var batchLines = 0;
        foreach (var fact in pending)
        {
            var lines = fact.Added + fact.Removed;
            if (lines > _options.MaxLinesPerObject || fact.Files.Count > GitDiffObjectManifest.MaxFiles)
            {
                _reader.Store(repo.Catalog, GitDiffObjectBuilder.TooLarge(GitDiffObjectKey.Commit(repo.Project, fact),
                    fact.Files, now));
                continue;
            }
            // A commit an earlier batch cut off at the output limit is read alone.
            if (repo.ReadAlone.Contains(fact.Sha))
            {
                batches.Add([fact]);
                continue;
            }
            if (batch.Count > 0 && (batch.Count >= _options.MaxPatchCommitsPerRead
                || batchLines + lines > _options.MaxPatchLinesPerRead))
            {
                batches.Add(batch);
                batch = [];
                batchLines = 0;
            }
            batch.Add(fact);
            batchLines += lines;
        }
        if (batch.Count > 0) batches.Add(batch);
        foreach (var next in batches)
        {
            if (OverBudget(clock)) deferred.AddRange(next.Select(fact => new GitCommitDiffWork(fact.Sha)));
            else ReadPatchBatch(repo, root, next, now, deferred);
        }

        foreach (var members in aggregates)
        {
            var key = GitDiffObjectKey.Aggregate(repo.Project, members.Select(member => member.Sha).ToList());
            if (Exists(repo, key.ObjectId)) continue;
            if (members.Any(member => !Exists(repo, GitDiffObjectKey.Commit(repo.Project, member).ObjectId)))
            {
                deferred.Add(new GitAggregateDiffWork(members.Select(member => member.Sha).ToList()));
                continue;
            }
            _reader.Store(repo.Catalog, GitDiffObjectBuilder.Aggregate(repo.Project, members, now));
        }
        return deferred;
    }

    /// <summary>
    /// One batched patch read. A commit cut off by the output limit is read
    /// again on its own; alone and still too large, it gets a manifest-only object.
    /// </summary>
    private void ReadPatchBatch(RepoFacts repo, string root, List<GitCommitFact> batch, DateTimeOffset now,
        List<GitFactWork> deferred)
    {
        var patches = GitFactCommands.ReadPatches(root, batch.Select(fact => fact.Sha).ToList());
        foreach (var fact in batch)
        {
            var key = GitDiffObjectKey.Commit(repo.Project, fact);
            if (patches.TryGetValue(fact.Sha, out var patch))
                _reader.Store(repo.Catalog, GitDiffObjectBuilder.FromCommit(repo.Project, fact, patch, now));
            else if (batch.Count == 1)
                _reader.Store(repo.Catalog, GitDiffObjectBuilder.TooLarge(key, fact.Files, now));
            else
            {
                repo.ReadAlone.Add(fact.Sha);
                deferred.Add(new GitCommitDiffWork(fact.Sha));
            }
        }
    }

    /// <summary>
    /// The member commits of an aggregate in order, without SHAs the repository
    /// does not have (the reader leaves them out too, so both derive one key).
    /// Null while a member's facts are still unknown.
    /// </summary>
    private static List<GitCommitFact>? Members(RepoFacts repo, GitAggregateDiffWork aggregate)
    {
        var members = new List<GitCommitFact>();
        foreach (var sha in aggregate.Shas)
        {
            if (repo.Catalog.Commit(sha) is { } fact) members.Add(fact);
            else if (!repo.Catalog.IsMissing(sha)) return null;
        }
        return members;
    }

    private static bool Exists(RepoFacts repo, string objectId) => repo.Catalog.Manifest(objectId) is not null;

    /// <summary>Loads the stored manifests of <paramref name="objectIds"/> into the catalog in one read.</summary>
    private void PrimeManifests(RepoFacts repo, IEnumerable<string> objectIds)
    {
        var unknown = objectIds.Where(id => repo.Catalog.Manifest(id) is null).Distinct(StringComparer.Ordinal).ToList();
        if (unknown.Count == 0 || _store is not { IsAvailable: true }) return;
        foreach (var manifest in _store.ReadDiffObjects(unknown).Values) repo.Catalog.AddManifest(manifest);
    }

    private void Backfill(RepoFacts repo, IReadOnlyList<TaskInfo> tasks, Stopwatch clock)
    {
        if (_backfill is null) return;
        var done = 0;
        foreach (var task in tasks)
        {
            if (done >= _options.MaxBackfillsPerPass || OverBudget(clock)) return;
            if (!CommitAttributionBackfill.Needs(task)) continue;
            if (!repo.BackfillAttempts.Add(task.TaskKey + "\0" + TaskGitSignature.For(task))) continue;
            done++;
            if (_backfill(task))
                _logger.LogInformation("git-card-facts-attribution-backfilled repository={Repository} task={Task}",
                    repo.Project, task.TaskKey);
        }
    }

    private void IndexMerges(RepoFacts repo, string root, IReadOnlyDictionary<string, string> refs,
        IReadOnlyDictionary<string, GitReachableSet> reach, string name, List<GitMergeIndexChange> changes,
        Dictionary<string, string> cursors)
    {
        var full = GitRefObjectIds.Resolve(refs, name);
        var tip = full is not null && GitReachabilityIndex.IsSha(refs[full]) ? refs[full] : null;
        repo.Merges.TryGetValue(name, out var known);
        if (known is not null && string.Equals(known.Tip, tip, StringComparison.OrdinalIgnoreCase)) return;
        if (tip is null)
        {
            repo.Merges[name] = new MergeIndex(null, []);
            changes.Add(new GitMergeIndexChange(name, true, []));
            cursors[MergeCursorPrefix + name] = "";
            return;
        }
        var fastForward = known?.Tip is { Length: > 0 } old && full is not null
            && reach.TryGetValue(full, out var set) && set.Tip == tip && set.Contains(old);
        if (fastForward)
        {
            var added = GitFactCommands.IntegrationMerges(root, tip, known!.Tip);
            repo.Merges[name] = new MergeIndex(tip, added.Concat(known.Merges).ToList());
            changes.Add(new GitMergeIndexChange(name, false, added));
        }
        else
        {
            var all = GitFactCommands.IntegrationMerges(root, tip, null);
            repo.Merges[name] = new MergeIndex(tip, all.ToList());
            changes.Add(new GitMergeIndexChange(name, true, all));
        }
        cursors[MergeCursorPrefix + name] = tip;
    }

    /// <summary>
    /// The project integration view (R12) from this pass's facts. Recomposed
    /// when the Git facts or the joined task facts changed; a task-only change
    /// starts no Git (the promotion files are cached per head pair).
    /// </summary>
    private GitStoredProjectView? ComposeView(RepoFacts repo, string root, IReadOnlyList<TaskInfo> tasks,
        TaskListGitProjection projection, IReadOnlyDictionary<string, string> refs, string integrationBranch,
        IReadOnlyDictionary<string, GitReachableSet> reach, Dictionary<string, string> cursors)
    {
        var integrationRef = OriginRef(integrationBranch);
        var releaseRef = OriginRef(ReleaseBranch);
        var integrationHead = Tip(refs, integrationRef);
        var releaseHead = Tip(refs, releaseRef);
        var accepted = ProjectIntegrationViewService.SelectAccepted(tasks);
        var statuses = projection.Integration;
        var publisher = repo.Merges.TryGetValue(integrationRef, out var merges) ? merges : new MergeIndex(null, []);
        var signature = ViewSignature(accepted, statuses, integrationRef, integrationHead, releaseHead, publisher.Tip,
            refs.ContainsKey("refs/remotes/origin/" + integrationBranch));
        if (repo.View is not null && string.Equals(signature, repo.ViewSignature, StringComparison.Ordinal)) return null;

        ProjectIntegrationView view;
        if (integrationHead is null)
        {
            view = ProjectIntegrationViewService.Empty(repo.Project, integrationRef, releaseRef,
                $"Integration ref '{integrationRef}' does not exist.");
        }
        else
        {
            var promotion = repo.Promotion is { } cached && cached.ReleaseHead == releaseHead
                && cached.IntegrationHead == integrationHead
                ? cached
                : Promote(root, releaseHead, integrationHead);
            if (!ReferenceEquals(promotion, repo.Promotion))
            {
                repo.Promotion = promotion;
                cursors[PromotionCursor] = JsonSerializer.Serialize(promotion);
            }
            view = ProjectIntegrationViewService.Compose(
                new ProjectIntegrationFacts(repo.Project, integrationRef, releaseRef, integrationHead, releaseHead,
                    reach.GetValueOrDefault("refs/remotes/origin/" + integrationBranch)?.Shas
                        ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    releaseHead is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        : reach.GetValueOrDefault("refs/remotes/origin/" + ReleaseBranch)?.Shas
                          ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    publisher.Merges, promotion.MergeBase, promotion.Files, _time.GetUtcNow().UtcDateTime),
                accepted, statuses);
        }
        repo.TaskGeneration++;
        repo.ViewSignature = signature;
        repo.View = new GitStoredProjectView(0, repo.TaskGeneration, signature, view, _time.GetUtcNow());
        return repo.View;
    }

    private static PromotionFacts Promote(string root, string? releaseHead, string integrationHead)
    {
        var mergeBase = releaseHead is null ? null : GitFactCommands.MergeBase(root, releaseHead, integrationHead);
        var files = mergeBase is null ? [] : GitFactCommands.RangeFiles(root, mergeBase, integrationHead)
            .Where(file => file.Status.Length > 0 && "ACDMR".Contains(file.Status[0]))
            .Select(file => new GitFileChange(file.Status, file.Path, file.Added, file.Removed))
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new PromotionFacts(releaseHead, integrationHead, mergeBase, files);
    }

    /// <summary>Everything the view joins, hashed: a change here recomposes it.</summary>
    private static string ViewSignature(IReadOnlyList<TaskInfo> accepted,
        IReadOnlyDictionary<string, TaskIntegrationStatus> statuses, string integrationRef, string? integrationHead,
        string? releaseHead, string? publisherTip, bool hasOriginBranch)
    {
        var text = new StringBuilder("view=1;")
            .Append(integrationRef).Append(';').Append(integrationHead).Append(';').Append(releaseHead).Append(';')
            .Append(publisherTip).Append(';').Append(hasOriginBranch).Append(';');
        foreach (var task in accepted.OrderBy(task => task.TaskKey, StringComparer.Ordinal))
        {
            statuses.TryGetValue(task.TaskKey, out var status);
            text.Append(task.TaskKey).Append('|').Append(task.Id).Append('|').Append(task.Key).Append('|')
                .Append(task.Title).Append('|').Append(task.State).Append('|')
                .Append(task.EnteredLaneAt.Ticks).Append('|')
                .Append(string.Join(',', TaskIntegrationStatusService.AttributedCommits(task))).Append('|')
                .Append(status?.Status).Append('|').Append(status?.Detail).Append('|');
            if (task.State == TaskStates.Archive && status?.Status == IntegrationStatuses.ConflictSkipped)
                text.Append(TaskListGitProjectionCache.SidecarStamp(ReviewSubjectStore.PathFor(task.FolderPath)).Hash);
            text.Append(';');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private string ResolveRoot(RepoFacts repo)
    {
        string? resolved = null;
        try { resolved = _resolveRoot(repo.WatchPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SilentCatch.Note(ex, "GitCardFactProducer: repository root resolution failed");
        }
        var root = string.IsNullOrWhiteSpace(resolved) ? repo.RepositoryPath : resolved;
        if (!WatchPathComparison.PathsEqual(root, repo.Root))
        {
            // The checkout resolved to another toplevel than the restore used:
            // carry the restored sets over so the index stays shared.
            var restored = TrackedRefs(repo.IntegrationBranch ?? "")
                .Select(fullRef => _reachability.Peek(repo.Root, fullRef))
                .Where(set => set is not null)
                .Select(set => (set!.FullRef, set.Tip, (IReadOnlyCollection<string>)set.Shas.ToArray()))
                .ToList();
            _reachability.Seed(root, restored);
            repo.Root = root;
        }
        return root;
    }

    private string ResolveIntegrationBranch(RepoFacts repo, string root, IReadOnlyDictionary<string, string> refs)
    {
        var configured = _configuredBranch(repo.Project)?.Trim() ?? "";
        // The configured branch wins when it exists locally or on origin; only
        // a missing branch asks Git for the repository's default branch.
        if (configured.Length > 0
            && (refs.ContainsKey("refs/heads/" + configured) || refs.ContainsKey("refs/remotes/origin/" + configured)))
            return configured;
        return _resolveIntegrationBranch(root, configured);
    }

    /// <summary>The integration and release refs whose reachability S3 keeps.</summary>
    internal static IReadOnlyList<string> TrackedRefs(string integrationBranch)
    {
        var refs = new List<string>
        {
            "refs/heads/" + ReleaseBranch,
            "refs/remotes/origin/" + ReleaseBranch,
        };
        if (!string.IsNullOrWhiteSpace(integrationBranch) && integrationBranch != ReleaseBranch)
        {
            refs.Add("refs/heads/" + integrationBranch);
            refs.Add("refs/remotes/origin/" + integrationBranch);
            refs.Add(GitService.IntegrationLaneRef(integrationBranch));
        }
        return refs;
    }

    private static string OriginRef(string branch)
        => branch.StartsWith("origin/", StringComparison.Ordinal) ? branch : "origin/" + branch;

    private static string? Tip(IReadOnlyDictionary<string, string> refs, string name)
        => GitRefObjectIds.Resolve(refs, name) is { } full && GitReachabilityIndex.IsSha(refs[full]) ? refs[full] : null;

    private static void Need(RepoFacts repo, HashSet<string> needed, string? sha)
    {
        if (!GitCardFactJoins.IsSha(sha)) return;
        if (repo.Catalog.Commit(sha!) is null && !repo.Catalog.IsMissing(sha!)) needed.Add(sha!.Trim());
    }

    private bool OverBudget(Stopwatch clock) => clock.Elapsed > _options.WorkBudget;

    private static bool SameRefs(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
        => a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value)
            && string.Equals(value, pair.Value, StringComparison.Ordinal));

    private GitPublishedFacts Pointer(RepoFacts repo, IReadOnlyDictionary<string, string> refs, string integrationBranch,
        IReadOnlyDictionary<string, GitReachableSet> reach, long generation, DateTimeOffset publishedAt, string state)
        => new(repo.Project, repo.WatchPath, repo.Root, generation, publishedAt, state, refs, integrationBranch,
            ReleaseBranch, reach,
            repo.RunCommits.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToArray(),
                StringComparer.Ordinal),
            repo.Merges.ToDictionary(pair => pair.Key,
                pair => (IReadOnlyList<GitIntegrationMergeCommit>)pair.Value.Merges.ToArray(), StringComparer.Ordinal),
            repo.View, repo.Catalog);

    /// <summary>
    /// Enables <c>core.commitGraph</c> and <c>fetch.writeCommitGraph</c> on a
    /// managed checkout, so a full reachability recompute stays bounded. Reads
    /// the local configuration file first; Git runs only when a setting is missing.
    /// </summary>
    private void EnableCommitGraph(string repositoryPath)
    {
        try
        {
            var gitDirectory = GitRefSignature.ResolveGitDirectory(repositoryPath);
            if (gitDirectory is null) return;
            var config = Path.Combine(GitRefSignature.ResolveCommonGitDirectory(gitDirectory), "config");
            var text = File.Exists(config) ? File.ReadAllText(config) : "";
            foreach (var (section, key, setting) in new[]
                     {
                         ("core", "commitgraph", "core.commitGraph"),
                         ("fetch", "writecommitgraph", "fetch.writeCommitGraph"),
                     })
            {
                if (GitLocalConfig.IsTrue(text, section, key)) continue;
                var result = GitFactCommands.SetLocalConfig(repositoryPath, setting, "true");
                if (!result.Success)
                    _logger.LogWarning("git-commit-graph-config-failed repository={Repository} setting={Setting} error={Error}",
                        repositoryPath, setting, result.StandardError.Trim());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "git-commit-graph-config-unavailable repository={Repository}", repositoryPath);
        }
    }

    private sealed record MergeIndex(string? Tip, List<GitIntegrationMergeCommit> Merges);

    private sealed record PromotionFacts(string? ReleaseHead, string IntegrationHead, string? MergeBase,
        List<GitFileChange> Files);

    private sealed class RepoFacts(string project, string watchPath, string repositoryPath)
    {
        private readonly object _queueGate = new();
        private readonly List<GitFactWork> _queue = [];
        private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (long Length, long Ticks, IReadOnlyList<RunRecord> Runs)> _runs =
            new(StringComparer.Ordinal);

        public string Project { get; } = project;
        public string WatchPath { get; } = watchPath;
        public string RepositoryPath { get; } = repositoryPath;
        public string Root { get; set; } = repositoryPath;
        public GitRepositoryFactCatalog Catalog { get; } = new();
        public Dictionary<string, List<string>> RunCommits { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, MergeIndex> Merges { get; } = new(StringComparer.Ordinal);
        public HashSet<string>? TrailerTips { get; set; }
        public string? IntegrationBranch { get; set; }
        public PromotionFacts? Promotion { get; set; }
        public GitStoredProjectView? View { get; set; }
        public string? ViewSignature { get; set; }
        public long TaskGeneration { get; set; }
        public long Generation { get; set; }
        public IReadOnlyDictionary<string, string>? LastRefs { get; set; }
        public HashSet<string> BackfillAttempts { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ReadAlone { get; } = new(StringComparer.OrdinalIgnoreCase);
        public GitPublishedFacts? Published { get; set; }
        public bool NeedsPass { get; set; }

        public int QueueLength
        {
            get { lock (_queueGate) return _queue.Count; }
        }

        /// <summary>Queues work not queued yet; true when something new was added.</summary>
        public bool Enqueue(IReadOnlyCollection<GitFactWork> work)
        {
            var added = false;
            lock (_queueGate)
            {
                foreach (var item in work)
                {
                    if (!_queued.Add(item.Key)) continue;
                    _queue.Add(item);
                    added = true;
                }
            }
            return added;
        }

        public IReadOnlyList<GitFactWork> Drain()
        {
            lock (_queueGate)
            {
                var drained = _queue.ToList();
                _queue.Clear();
                _queued.Clear();
                return drained;
            }
        }

        public void Requeue(IReadOnlyList<GitFactWork> work) => Enqueue(work);

        /// <summary>A card's runs with captured SHAs, re-read only when its session events file changed.</summary>
        public IReadOnlyList<RunRecord> Runs(TaskInfo task)
        {
            if (string.IsNullOrWhiteSpace(task.FolderPath)) return [];
            var file = new FileInfo(TaskPaths.SessionEventsLog(task.FolderPath));
            var length = file.Exists ? file.Length : -1;
            var ticks = file.Exists ? file.LastWriteTimeUtc.Ticks : 0;
            if (_runs.TryGetValue(task.FolderPath, out var cached) && cached.Length == length && cached.Ticks == ticks)
                return cached.Runs;
            IReadOnlyList<RunRecord> runs = length < 0 ? []
                : RunTimelineBuilder.Build(TaskSessionLog.ReadSessionEventsInFolder(task.FolderPath), [], DateTime.UtcNow).Runs;
            _runs[task.FolderPath] = (length, ticks, runs);
            return runs;
        }
    }
}

/// <summary>Reads boolean settings from a local Git configuration file without starting Git.</summary>
internal static class GitLocalConfig
{
    /// <summary>
    /// True when <c>[section] key = true</c> appears in <paramref name="text"/>.
    /// Section and key names are case-insensitive, as Git treats them.
    /// </summary>
    internal static bool IsTrue(string text, string section, string key)
    {
        var current = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[')
            {
                var end = line.IndexOf(']');
                current = end > 0 ? line[1..end].Trim() : "";
                continue;
            }
            if (!string.Equals(current, section, StringComparison.OrdinalIgnoreCase)) continue;
            var equals = line.IndexOf('=');
            var name = (equals < 0 ? line : line[..equals]).Trim();
            if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) continue;
            var value = equals < 0 ? "true" : line[(equals + 1)..].Trim();
            return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value is "1" or "yes" or "on";
        }
        return false;
    }
}
