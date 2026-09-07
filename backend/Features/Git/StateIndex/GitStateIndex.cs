using System.Collections.Concurrent;
using System.Diagnostics;

namespace AgentStudio.Git;

/// <summary>
/// The one background git index (AGT-2726). It owns every repository's
/// git-derived state - HEAD, branch tips, worktree list, project inventory -
/// and is the only place that forks git for those reads.
///
/// <para>
/// Request paths call <see cref="Snapshot"/> or <see cref="InventoryFor"/> and
/// return the last completed capture immediately, stamped with
/// <see cref="Stamp"/>. They never wait for a run: the board endpoints used to
/// compute this state under a process-wide gate, which turned six refreshes a
/// minute into 72 git processes a minute and left <c>tasks/grouped</c> blocked
/// for seconds at a time with no git work of its own.
/// </para>
///
/// <para>
/// Runs are change-driven: <see cref="Request"/> records a trigger, a debounce
/// window coalesces a burst, and <see cref="RunDueAsync"/> starts at most
/// <see cref="MaxConcurrentRepositories"/> repository captures at a time. A
/// repository already running is never started twice; its trigger is folded
/// into the next run. Each capture runs on a dedicated long-running thread, so
/// a slow repository cannot starve the thread pool that serves requests.
/// </para>
/// </summary>
public sealed class GitStateIndex
{
    /// <summary>
    /// Burst window after a trigger. Git rewrites several files per ref update,
    /// and an integration run touches many refs in a row; the window folds those
    /// into one capture while keeping the index inside the ten-second freshness
    /// budget.
    /// </summary>
    internal static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Upper bound on how long the debounce may keep deferring a capture. A
    /// repository under continuous ref churn would otherwise never settle, and
    /// the index has a ten-second freshness budget to meet.
    /// </summary>
    internal static readonly TimeSpan MaxDebounceWait = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Safety refresh for repository layouts a filesystem watcher cannot
    /// observe. This is a backstop, not the normal invalidation path.
    /// </summary>
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A capture older than this marks the response stale even when no trigger
    /// is pending, so a watcher that silently died still shows up in the UI.
    /// </summary>
    internal static readonly TimeSpan FreshnessBudget = TimeSpan.FromMinutes(15);

    /// <summary>A run slower than this is logged with its slowest git command.</summary>
    internal static readonly TimeSpan SlowRunThreshold = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Cross-repository spawn budget. Two at a time keeps the operator laptop
    /// usable while still clearing a multi-repository workspace promptly.
    /// </summary>
    internal const int MaxConcurrentRepositories = 2;

    private const int RunHistoryLimit = 64;

    private readonly Func<GitIndexedRepository, GitRepositoryState> _capture;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, RepositoryEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _rootByProject =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<GitIndexRunRecord> _runs = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrentRepositories, MaxConcurrentRepositories);
    private long _generation;

    public GitStateIndex(
        GitService git,
        ILogger<GitStateIndex> logger)
        : this(repository => CaptureWith(git, repository), logger, TimeProvider.System)
    {
    }

    internal GitStateIndex(
        Func<GitIndexedRepository, GitRepositoryState> capture,
        ILogger logger,
        TimeProvider time)
    {
        _capture = capture;
        _logger = logger;
        _time = time;
    }

    /// <summary>
    /// Monotonic stamp that advances whenever any repository capture completes
    /// with new state. Downstream projections fold it into their input version
    /// so a ref move invalidates them without a second watcher.
    /// </summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>Raised after a capture changed a repository's state.</summary>
    public event Action<string>? RepositoryIndexed;

    /// <summary>
    /// Adds a repository to the index, or attaches another project handle to an
    /// already-tracked repository. A newly registered repository is immediately
    /// pending so the first tick captures it.
    /// </summary>
    public void Register(string repositoryRoot, string projectName)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot)) return;
        var key = NormalizeRoot(repositoryRoot);
        var entry = _entries.GetOrAdd(key, static root => new RepositoryEntry(root));
        if (!string.IsNullOrWhiteSpace(projectName))
        {
            lock (entry.Gate) entry.Projects.Add(projectName);
            _rootByProject[projectName] = key;
        }
        Request(key, GitIndexTriggers.Startup);
    }

    /// <summary>The last completed capture for a repository, or null when it has never run.</summary>
    public GitRepositoryState? Snapshot(string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot)) return null;
        if (!_entries.TryGetValue(NormalizeRoot(repositoryRoot), out var entry)) return null;
        lock (entry.Gate) return entry.Snapshot;
    }

    /// <summary>
    /// The last captured inventory for a project. Returns null when the index has
    /// not captured that project yet; the caller renders the loading state and
    /// the SignalR push delivers the inventory once the capture lands.
    /// </summary>
    public GitProjectInventory? InventoryFor(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName)) return null;
        if (!_rootByProject.TryGetValue(projectName, out var root)) return null;
        var snapshot = Snapshot(root);
        return snapshot is not null
            && snapshot.InventoryByProject.TryGetValue(projectName, out var inventory)
            ? inventory
            : null;
    }

    /// <summary>Records a trigger for one repository. Concurrent triggers coalesce.</summary>
    public void Request(string repositoryRoot, string trigger)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot)) return;
        var key = NormalizeRoot(repositoryRoot);
        if (!_entries.TryGetValue(key, out var entry)) return;
        lock (entry.Gate)
        {
            var now = _time.GetUtcNow();
            entry.PendingTrigger = trigger;
            entry.PendingSince = now;
            entry.PendingFirstSince ??= now;
        }
    }

    /// <summary>Records a trigger for every tracked repository.</summary>
    public void RequestAll(string trigger)
    {
        foreach (var key in _entries.Keys) Request(key, trigger);
    }

    /// <summary>
    /// Marks every repository whose last run is older than the sweep interval as
    /// pending. Called by the hosted service; separate from <see cref="RequestAll"/>
    /// so an idle workspace does not re-capture on every tick.
    /// </summary>
    public void RequestSweep()
    {
        var now = _time.GetUtcNow();
        foreach (var entry in _entries.Values)
        {
            lock (entry.Gate)
            {
                if (entry.PendingTrigger is not null) continue;
                if (!GitIndexRunPolicy.IsSweepDue(entry.LastRunAt, now, SweepInterval)) continue;
                entry.PendingTrigger = GitIndexTriggers.Sweep;
                entry.PendingSince = now;
                entry.PendingFirstSince ??= now;
            }
        }
    }

    /// <summary>
    /// Freshness stamp for a response. <c>gitStateAt</c> is the oldest capture
    /// across the tracked repositories and <c>stale</c> says at least one of them
    /// has a change the index has not folded in yet.
    /// </summary>
    public GitStateStamp Stamp()
    {
        DateTimeOffset? oldest = null;
        var stale = false;
        var now = _time.GetUtcNow();
        var any = false;

        foreach (var entry in _entries.Values)
        {
            any = true;
            lock (entry.Gate)
            {
                if (entry.Snapshot is null)
                {
                    stale = true;
                    continue;
                }
                var capturedAt = entry.Snapshot.CapturedAtUtc;
                if (oldest is null || capturedAt < oldest) oldest = capturedAt;
                if (entry.PendingTrigger is not null || entry.Running) stale = true;
                if (now - capturedAt > FreshnessBudget) stale = true;
            }
        }

        return any ? new GitStateStamp(oldest, stale) : GitStateStamp.Unknown;
    }

    /// <summary>Per-repository index age and last run, for the Admin page.</summary>
    public IReadOnlyList<GitIndexRepositoryStatus> Status()
    {
        var now = _time.GetUtcNow();
        var result = new List<GitIndexRepositoryStatus>();
        foreach (var entry in _entries.Values)
        {
            lock (entry.Gate)
            {
                result.Add(new GitIndexRepositoryStatus(
                    entry.Root,
                    entry.Projects.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList(),
                    entry.Snapshot?.CapturedAtUtc,
                    entry.Snapshot is null ? null : (now - entry.Snapshot.CapturedAtUtc).TotalSeconds,
                    entry.LastTrigger,
                    entry.LastRunMs,
                    entry.LastRunSpawns,
                    entry.PendingTrigger is not null || entry.Running));
            }
        }
        return result.OrderBy(item => item.RepositoryRoot, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The most recent completed runs, newest last.</summary>
    public IReadOnlyList<GitIndexRunRecord> RecentRuns() => _runs.ToArray();

    /// <summary>
    /// One scheduling tick: starts every repository whose debounce has elapsed,
    /// up to the cross-repository budget, and awaits the captures it started.
    /// Deterministic, so tests drive it directly instead of racing the loop.
    /// </summary>
    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        var started = new List<Task>();
        var now = _time.GetUtcNow();

        foreach (var entry in _entries.Values)
        {
            if (cancellationToken.IsCancellationRequested) break;

            string trigger;
            lock (entry.Gate)
            {
                var debounceElapsed =
                    (entry.PendingSince is { } since && now - since >= Debounce)
                    || (entry.PendingFirstSince is { } first && now - first >= MaxDebounceWait);
                if (!GitIndexRunPolicy.ShouldStartRun(
                        entry.PendingTrigger is not null,
                        entry.Running,
                        debounceElapsed,
                        _slots.CurrentCount > 0))
                    continue;

                // Take the cross-repository slot here rather than inside the run,
                // so a repository over budget simply stays pending for the next
                // tick instead of parking a dedicated thread on a wait.
                if (!_slots.Wait(0, CancellationToken.None)) continue;

                trigger = entry.PendingTrigger!;
                entry.PendingTrigger = null;
                entry.PendingSince = null;
                entry.PendingFirstSince = null;
                entry.Running = true;
            }

            started.Add(RunRepositoryAsync(entry, trigger));
        }

        if (started.Count > 0) await Task.WhenAll(started);
    }

    private Task RunRepositoryAsync(RepositoryEntry entry, string trigger)
        // A capture blocks on git subprocesses. Running it on a dedicated thread
        // instead of the thread pool is the point of the index: the pool stays
        // free for Kestrel, which is what made tasks/grouped wait seconds behind
        // refreshes that spawned nothing on its own behalf.
        => Task.Factory.StartNew(
            () => RunRepository(entry, trigger),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private void RunRepository(RepositoryEntry entry, string trigger)
    {
        var stopwatch = Stopwatch.StartNew();
        GitRepositoryState? captured = null;
        var spawns = 0;
        (string Command, int Count, long Ms)? slowest = null;

        try
        {
            GitIndexedRepository repository;
            lock (entry.Gate)
                repository = new GitIndexedRepository(entry.Root, entry.Projects.ToList());

            using (GitBackgroundWork.Begin())
            using (GitProcessTelemetry.BeginRequest("git/index-run", _logger, includeNested: true))
            {
                captured = _capture(repository) with { CapturedAtUtc = _time.GetUtcNow() };
                spawns = GitProcessTelemetry.CurrentTally()?.Spawns ?? 0;
                slowest = GitProcessTelemetry.CurrentSlowestCommand();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "git-index-run failed for repository {Repository}.", entry.Root);
        }
        finally
        {
            stopwatch.Stop();
            _slots.Release();
        }

        var changed = false;
        lock (entry.Gate)
        {
            if (captured is not null)
            {
                changed = entry.Snapshot is null || !StateEquivalent(entry.Snapshot, captured);
                entry.Snapshot = captured;
            }
            entry.Running = false;
            entry.LastRunAt = _time.GetUtcNow();
            entry.LastTrigger = trigger;
            entry.LastRunMs = stopwatch.ElapsedMilliseconds;
            entry.LastRunSpawns = spawns;
        }

        RecordRun(new GitIndexRunRecord(
            entry.Root,
            trigger,
            _time.GetUtcNow(),
            stopwatch.ElapsedMilliseconds,
            spawns,
            captured is not null));

        if (stopwatch.Elapsed >= SlowRunThreshold)
        {
            _logger.LogWarning(
                "git-index-run repository={Repository} spawns={Spawns} ms={ElapsedMs} trigger={Trigger} slowest={Slowest}",
                entry.Root,
                spawns,
                stopwatch.ElapsedMilliseconds,
                trigger,
                slowest is { } slow ? $"{slow.Command}x{slow.Count}={slow.Ms}ms" : "none");
        }
        else
        {
            _logger.LogInformation(
                "git-index-run repository={Repository} spawns={Spawns} ms={ElapsedMs} trigger={Trigger}",
                entry.Root,
                spawns,
                stopwatch.ElapsedMilliseconds,
                trigger);
        }

        if (!changed) return;
        Interlocked.Increment(ref _generation);
        try
        {
            RepositoryIndexed?.Invoke(entry.Root);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "GitStateIndex: repository-indexed subscriber");
        }
    }

    private void RecordRun(GitIndexRunRecord record)
    {
        _runs.Enqueue(record);
        while (_runs.Count > RunHistoryLimit && _runs.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Compares the two captures on the facts the index projects. Capture time
    /// alone must not advance the generation, otherwise the safety sweep would
    /// invalidate every downstream projection on an idle workspace.
    /// </summary>
    private static bool StateEquivalent(GitRepositoryState left, GitRepositoryState right)
    {
        if (!string.Equals(left.Head, right.Head, StringComparison.OrdinalIgnoreCase)) return false;
        if (left.BranchTips.Count != right.BranchTips.Count) return false;
        foreach (var (branch, tip) in left.BranchTips)
        {
            if (!right.BranchTips.TryGetValue(branch, out var other)) return false;
            if (!string.Equals(tip, other, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (left.Worktrees.Count != right.Worktrees.Count) return false;
        for (var i = 0; i < left.Worktrees.Count; i++)
        {
            if (!string.Equals(left.Worktrees[i].Path, right.Worktrees[i].Path, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.Equals(left.Worktrees[i].HeadSha, right.Worktrees[i].HeadSha, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static GitRepositoryState CaptureWith(GitService git, GitIndexedRepository repository)
    {
        var inventories = new Dictionary<string, GitProjectInventory>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in repository.ProjectNames)
        {
            var inventory = git.ComputeProjectInventoryUncached(project);
            if (inventory is not null) inventories[project] = inventory;
        }

        // HEAD, branch tips and worktrees are already resolved inside the
        // inventory read. Deriving them here keeps the capture at one bounded
        // set of git reads per project rather than re-forking for each fact.
        var primary = inventories.Values.FirstOrDefault(inventory => inventory.IsRepo);
        var branchTips = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var branch in primary?.Branches ?? [])
        {
            if (!string.IsNullOrWhiteSpace(branch.TipSha)) branchTips[branch.Name] = branch.TipSha;
        }

        return new GitRepositoryState(
            repository.RepositoryRoot,
            // Stamped by the index once the capture completes; the delegate does
            // not own the clock.
            DateTimeOffset.MinValue,
            primary?.CurrentBranch is { } current && branchTips.TryGetValue(current, out var head) ? head : null,
            branchTips,
            primary?.Worktrees ?? [],
            inventories);
    }

    private static string NormalizeRoot(string root)
    {
        try
        {
            return Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            SilentCatch.Note(ex, "GitStateIndex: invalid repository root");
            return root.TrimEnd('/', '\\');
        }
    }

    private sealed class RepositoryEntry(string root)
    {
        public object Gate { get; } = new();
        public string Root { get; } = root;
        public HashSet<string> Projects { get; } = new(StringComparer.OrdinalIgnoreCase);
        public GitRepositoryState? Snapshot { get; set; }
        public bool Running { get; set; }
        public string? PendingTrigger { get; set; }
        public DateTimeOffset? PendingSince { get; set; }
        public DateTimeOffset? PendingFirstSince { get; set; }
        public DateTimeOffset? LastRunAt { get; set; }
        public string? LastTrigger { get; set; }
        public long LastRunMs { get; set; }
        public int LastRunSpawns { get; set; }
    }
}

/// <summary>Per-repository index health for the Admin page.</summary>
public sealed record GitIndexRepositoryStatus(
    string RepositoryRoot,
    IReadOnlyList<string> ProjectNames,
    DateTimeOffset? GitStateAt,
    double? AgeSeconds,
    string? LastTrigger,
    long LastRunMs,
    int LastRunSpawns,
    bool Refreshing);
