using System.Collections.Concurrent;

namespace AgentStudio.Git;

/// <summary>
/// The board's git-derived state as a background index, one entry per
/// repository (AGT-2726).
///
/// <para>
/// Before this existed, git-derived board state was computed lazily from
/// whatever request happened to miss the cache, under a process-wide gate.
/// Every refresh trigger repeated the work, the gate serialized it, and the
/// endpoints that own no git work at all (<c>tasks/grouped</c>, <c>tasks/list</c>)
/// spent their wall-clock waiting behind it - measured at 2,775 s of wait for
/// 351 grouped calls with zero git spawns of their own.
/// </para>
///
/// <para>
/// This type owns the scheduling half of the fix and nothing else. It records
/// which repositories exist, which have pending changes, which are currently
/// being indexed, and when each last completed. It never starts a process and
/// never blocks: <see cref="Signal"/> marks a repository dirty,
/// <see cref="Read"/> and <see cref="ReadBoard"/> answer request paths from the
/// last completed run. <see cref="GitStateIndexHostedService"/> owns the timers,
/// the watchers, and the bounded process execution.
/// </para>
/// </summary>
public sealed class GitStateIndex
{
    private readonly ConcurrentDictionary<string, RepositoryEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;
    private readonly GitStateIndexOptions _options;

    public GitStateIndex(GitStateIndexOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Sanitized();
        _time = timeProvider ?? TimeProvider.System;
    }

    internal GitStateIndexOptions Options => _options;

    /// <summary>
    /// Declares a repository the index is responsible for. Idempotent, and safe
    /// to call on every sweep: a repository that is already registered keeps
    /// its snapshot and its schedule. A newly registered repository starts
    /// dirty so it is indexed once at startup rather than waiting a sweep.
    /// </summary>
    public void Register(string repositoryRoot, string projectName)
    {
        var key = Normalize(repositoryRoot);
        if (key is null) return;
        _entries.GetOrAdd(key, _ => new RepositoryEntry(key, projectName));
    }

    /// <summary>
    /// Records that this repository's git-derived state may have changed. Called
    /// from the ref watcher, from the Task Server's own integration and delivery
    /// events, and from the safety sweep. Cheap and non-blocking by contract -
    /// request paths call it too.
    /// </summary>
    public void Signal(string repositoryRoot, string trigger)
    {
        var key = Normalize(repositoryRoot);
        if (key is null || !_entries.TryGetValue(key, out var entry)) return;
        entry.MarkDirty(trigger, _time.GetUtcNow());
    }

    /// <summary>Signals every registered repository (used by the safety sweep).</summary>
    public void SignalAll(string trigger)
    {
        var now = _time.GetUtcNow();
        foreach (var entry in _entries.Values) entry.MarkDirty(trigger, now);
    }

    /// <summary>
    /// The stamp for one repository. Never spawns git, never waits on a run.
    /// </summary>
    public GitStateStamp Read(string repositoryRoot)
    {
        var key = Normalize(repositoryRoot);
        return key is not null && _entries.TryGetValue(key, out var entry)
            ? entry.Stamp()
            : GitStateStamp.Warming;
    }

    /// <summary>
    /// The stamp a cross-project board answer carries: oldest completed run,
    /// stale if any repository is behind. <see cref="GitStateStamp.Warming"/>
    /// when no repository is registered (a workspace with no git project).
    /// </summary>
    public GitStateStamp ReadBoard()
        => GitStateStamp.Merge(_entries.Values.Select(entry => entry.Stamp()));

    /// <summary>Per-repository facts for the Admin telemetry panel.</summary>
    public IReadOnlyList<GitIndexRepositoryStatus> Status()
    {
        var now = _time.GetUtcNow();
        return _entries.Values
            .Select(entry => entry.Status(now))
            .OrderBy(status => status.Repository, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Repositories admitted to start a run right now, already marked as
    /// running. The caller owns exactly one <see cref="Complete"/> per claim.
    /// </summary>
    internal IReadOnlyList<GitIndexClaim> ClaimDue()
    {
        var now = _time.GetUtcNow();
        var claims = new List<GitIndexClaim>();
        foreach (var entry in _entries.Values)
        {
            if (entry.TryClaim(now, _options, out var claim)) claims.Add(claim);
        }
        return claims;
    }

    /// <summary>Publishes the result of one run and releases the repository.</summary>
    internal void Complete(GitIndexClaim claim, int spawns, long elapsedMs, string? slowestCommand)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (!_entries.TryGetValue(claim.RepositoryRoot, out var entry)) return;
        entry.Complete(claim, _time.GetUtcNow(), spawns, elapsedMs, slowestCommand);
    }

    /// <summary>
    /// Releases a claim whose run never ran (the executor refused it during
    /// shutdown). Deliberately not <see cref="Complete"/>: publishing a
    /// completion time for work that did not happen would make the stamp claim a
    /// freshness the index never earned. The repository goes back to dirty so
    /// the trigger that earned the claim survives.
    /// </summary>
    internal void Abandon(GitIndexClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (!_entries.TryGetValue(claim.RepositoryRoot, out var entry)) return;
        entry.Abandon(claim.Trigger);
    }

    internal static string? Normalize(string? repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot)) return null;
        try
        {
            return Path.GetFullPath(repositoryRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            SilentCatch.Note(ex, "GitStateIndex: invalid repository root");
            return null;
        }
    }

    private sealed class RepositoryEntry(string root, string projectName)
    {
        private readonly Lock _gate = new();
        private DateTimeOffset? _completedAt;
        private DateTimeOffset _lastRunStartedAt = DateTimeOffset.MinValue;

        // Registration is not a change burst, so the startup run is due
        // immediately rather than after a debounce window it has no reason to
        // wait out. Every later trigger stamps a real time here.
        private DateTimeOffset _dirtyAt = DateTimeOffset.MinValue;
        private bool _dirty = true;
        private bool _running;
        private string _pendingTrigger = "startup";
        private string _lastTrigger = "none";
        private int _lastSpawns;
        private long _lastElapsedMs;
        private string? _lastSlowestCommand;
        private long _runs;

        public GitStateStamp Stamp()
        {
            lock (_gate) return new GitStateStamp(_completedAt, _dirty || _running || _completedAt is null);
        }

        public void MarkDirty(string trigger, DateTimeOffset now)
        {
            lock (_gate)
            {
                _dirty = true;
                _dirtyAt = now;
                _pendingTrigger = trigger;
            }
        }

        public bool TryClaim(DateTimeOffset now, GitStateIndexOptions options, out GitIndexClaim claim)
        {
            claim = null!;
            lock (_gate)
            {
                var sweepDue = now - Latest(_completedAt, _lastRunStartedAt) >= options.SweepInterval;
                var admission = GitIndexAdmissionPolicy.Admit(
                    _running,
                    _dirty,
                    now - _dirtyAt >= options.Debounce,
                    sweepDue);
                if (admission != GitIndexAdmission.Start) return false;

                var trigger = _dirty ? _pendingTrigger : "sweep";
                // Clearing the dirty bit here (not on completion) is what makes a
                // change arriving DURING the run mark the repository dirty again
                // instead of being swallowed by the run that started before it.
                _dirty = false;
                _running = true;
                _lastRunStartedAt = now;
                claim = new GitIndexClaim(root, projectName, trigger, now);
                return true;
            }
        }

        public void Complete(
            GitIndexClaim claim,
            DateTimeOffset now,
            int spawns,
            long elapsedMs,
            string? slowestCommand)
        {
            lock (_gate)
            {
                _running = false;
                _completedAt = now;
                _lastTrigger = claim.Trigger;
                _lastSpawns = spawns;
                _lastElapsedMs = elapsedMs;
                _lastSlowestCommand = slowestCommand;
                _runs++;
            }
        }

        public void Abandon(string trigger)
        {
            lock (_gate)
            {
                _running = false;
                _dirty = true;
                _dirtyAt = DateTimeOffset.MinValue;
                _pendingTrigger = trigger;
            }
        }

        public GitIndexRepositoryStatus Status(DateTimeOffset now)
        {
            lock (_gate)
            {
                return new GitIndexRepositoryStatus(
                    root,
                    projectName,
                    _completedAt,
                    _completedAt is null ? null : (now - _completedAt.Value).TotalSeconds,
                    _dirty || _running || _completedAt is null,
                    _running,
                    _lastTrigger,
                    _lastSpawns,
                    _lastElapsedMs,
                    _lastSlowestCommand,
                    _runs);
            }
        }

        private static DateTimeOffset Latest(DateTimeOffset? a, DateTimeOffset b)
            => a is null ? b : a.Value > b ? a.Value : b;
    }
}

/// <summary>One admitted run. Held by the runner between claim and completion.</summary>
internal sealed record GitIndexClaim(
    string RepositoryRoot,
    string ProjectName,
    string Trigger,
    DateTimeOffset StartedAt);

/// <summary>Per-repository index facts rendered on the Admin telemetry panel.</summary>
public sealed record GitIndexRepositoryStatus(
    string Repository,
    string ProjectName,
    DateTimeOffset? IndexedAt,
    double? IndexAgeSeconds,
    bool Stale,
    bool Running,
    string LastTrigger,
    int LastSpawns,
    long LastElapsedMs,
    string? LastSlowestCommand,
    long Runs);
