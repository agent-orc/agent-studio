using Microsoft.Extensions.Configuration;
using AgentStudio.Retention;

namespace AgentStudio.Pipeline;

/// <summary>
/// The debounced core of the Transition-Committer. Buckets enqueued lane
/// transitions by workspace git root and flushes a single evidence commit per
/// repo once the repo has been quiet for <c>WorkspaceEvidence:DebounceSeconds</c>
/// or has been accumulating for <c>WorkspaceEvidence:MaxDelaySeconds</c>
/// (whichever comes first). Stateful but timing-pure: it never sleeps and reads
/// "now" from an injected <see cref="TimeProvider"/>, so
/// <see cref="WorkspaceEvidenceWorker"/> drives cadence in production while the
/// tests drive it deterministically with a <c>FakeTimeProvider</c>.
///
/// <para>All git work is delegated to <see cref="WorkspaceArtifactCommitService"/>
/// (shared committer identity, per-repo lock, index.lock retry) rather than a
/// parallel implementation. Scoping is allow-list by construction: only the
/// touched <c>projects/&lt;name&gt;</c> watch-path folders are staged, so the
/// <em>repo-root</em> runtime noise (<c>identities/</c>, <c>telemetry/</c>,
/// <c>logs/</c>, <c>adhoc-usage.jsonl</c>) is excluded without depending on
/// <c>.gitignore</c>. Runtime state that lives <em>inside</em> a project folder
/// — chiefly the tracked, high-churn <c>projects/&lt;name&gt;/.orchestrator/</c>
/// session/event files — is NOT handled by scoping and is dropped by the
/// <see cref="ExcludeGlobs"/>, which the committer applies as <c>:(exclude)</c>
/// pathspecs so the exclusion holds even for already-tracked files.</para>
/// </summary>
public sealed class WorkspaceEvidenceBatcher
{
    // Scratch + per-project runtime excludes. Deliberately NOT identities/
    // telemetry/logs by name: a project can legitimately be called "logs"
    // (projects/logs holds drift analysis), and the repo-root runtime dirs are
    // already outside every projects/<name> pathspec we stage. The committer
    // enforces each glob both by unstaging it (`git reset`) and as a
    // `:(exclude)` pathspec on the partial commit (the half that holds for
    // already-tracked files); both use default magic (where * also matches /),
    // so a glob matches at any depth under the staged projects/<name> folders.
    //
    // `*/.orchestrator/*` is load-bearing, not defense-in-depth: the
    // per-project .orchestrator/ runtime (orchestrator.jsonl ~1 MB and growing,
    // orchestrator-session.json, orchestrator-chat.jsonl, chat-attachments/*.png)
    // lives INSIDE projects/<name> and is tracked (not gitignored) in the live
    // workspace repo, so scoping alone cannot keep this high-churn session state
    // out of the evidence commits — only this exclude can. (`*/attachments/*`
    // does not match `/chat-attachments/`; the nested .orchestrator glob covers
    // those PNGs.)
    private static readonly string[] DefaultExcludeGlobs =
    {
        "*.tmp",
        "*.cache",
        "*/.orchestrator/*",
        "*/attachments/*",
        "*/results/*",
        "*/.runtime/*",
        "*/logs/cli-output.log",
        "*/logs/cli-output.log.1",
        "*/review-*.log",
        "*/review/*stdout*",
    };

    private readonly WorkspaceArtifactCommitService _commit;
    private readonly WorkspaceArtifactPushQueue? _push;
    private readonly IConfiguration _config;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ArtifactClassifier _classifier = new();

    private readonly object _lock = new();
    private readonly Dictionary<string, PendingRepo> _pending = new(StringComparer.OrdinalIgnoreCase);

    // Resolved git root of the configured TaskRepository, cached. When set, it
    // is the ONLY repo the committer may touch: watch paths that resolve to any
    // other git root (e.g. the foreign source repos behind the
    // <c>&lt;project&gt;/.orchestrator/jobs</c> watch paths) are dropped so the
    // agent-orchestrator bot identity can never write evidence commits — or, with
    // Push on, pushes — into a developer's source history. Left null (guard off)
    // only when TaskRepository is unset, preserving the batcher's use as a
    // primitive in tests.
    private readonly object _rootLock = new();
    private string? _taskRepoRoot;
    private bool _taskRepoRootResolved;

    public WorkspaceEvidenceBatcher(
        WorkspaceArtifactCommitService commit,
        IConfiguration config,
        ILogger logger,
        TimeProvider? time = null,
        WorkspaceArtifactPushQueue? push = null)
    {
        _commit = commit;
        _config = config;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _push = push;
    }

    public bool Enabled => _config.GetValue<bool?>("WorkspaceEvidence:Enabled") ?? true;

    private bool PushEnabled => _config.GetValue<bool?>("WorkspaceEvidence:Push") ?? false;

    private TimeSpan Debounce => TimeSpan.FromSeconds(
        Math.Clamp(_config.GetValue<int?>("WorkspaceEvidence:DebounceSeconds") ?? 15, 1, 3600));

    private TimeSpan MaxDelay
    {
        get
        {
            var debounce = _config.GetValue<int?>("WorkspaceEvidence:DebounceSeconds") ?? 15;
            var max = _config.GetValue<int?>("WorkspaceEvidence:MaxDelaySeconds") ?? 60;
            // The idle window can never exceed the hard cap; clamp so a
            // misconfiguration cannot make MaxDelay meaningless.
            return TimeSpan.FromSeconds(Math.Clamp(max, Math.Max(1, debounce), 24 * 60 * 60));
        }
    }

    private IReadOnlyList<string> ExcludeGlobs
    {
        get
        {
            var configured = _config.GetSection("WorkspaceEvidence:ExcludeGlobs").Get<string[]>();
            return configured is { Length: > 0 } ? configured : DefaultExcludeGlobs;
        }
    }

    internal bool IsIntermediateCommitExcluded(string relativePath)
        => _classifier.Classify(relativePath).ArtifactClass is ArtifactClass.HeavyWorkingData or ArtifactClass.Runtime;

    /// <summary>
    /// Fold one transition into its repo's pending bucket. Resolves the git root
    /// (dropping transitions whose watch path is outside any repo) and stamps the
    /// idle/first-seen cursors from virtual time.
    /// </summary>
    public void Ingest(WorkspaceEvidenceRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.WatchPath)) return;

        string? gitRoot;
        try { gitRoot = _commit.ResolveWorkspaceGitRoot(request.WatchPath); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "workspace-evidence ingest could not resolve git root for {WatchPath}", request.WatchPath);
            return;
        }
        if (gitRoot == null)
        {
            _logger.LogDebug("workspace-evidence ingest skipped: {WatchPath} is not under a git repo", request.WatchPath);
            return;
        }
        if (!IsTaskRepoRoot(gitRoot))
        {
            _logger.LogDebug(
                "workspace-evidence ingest skipped: {WatchPath} resolves to {GitRoot}, outside the task repository",
                request.WatchPath, gitRoot);
            return;
        }

        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (!_pending.TryGetValue(gitRoot, out var repo))
            {
                repo = new PendingRepo(now);
                _pending[gitRoot] = repo;
            }
            repo.Last = now;
            repo.Count++;
            repo.WatchPaths.Add(request.WatchPath);
            if (repo.Items.Count < 500)
                repo.Items.Add((request.ProjectName ?? string.Empty, request.Slug ?? string.Empty));
        }
    }

    /// <summary>
    /// Commit every repo whose debounce window (idle since last transition) or
    /// hard max-delay cap (since first transition) has elapsed. Called on each
    /// worker tick; safe to call when nothing is due (returns empty).
    /// </summary>
    public IReadOnlyList<WorkspaceEvidenceFlushResult> FlushDue()
    {
        var now = _time.GetUtcNow();
        var debounce = Debounce;
        var maxDelay = MaxDelay;

        var due = new List<KeyValuePair<string, PendingRepo>>();
        lock (_lock)
        {
            foreach (var kv in _pending)
            {
                var idle = now - kv.Value.Last;
                var total = now - kv.Value.First;
                if (idle >= debounce || total >= maxDelay)
                    due.Add(kv);
            }
            foreach (var kv in due) _pending.Remove(kv.Key);
        }

        return CommitBuckets(due);
    }

    /// <summary>
    /// Commit every pending repo immediately, ignoring the debounce/max-delay
    /// windows. Called on graceful shutdown so evidence still inside an open
    /// debounce window at stop time is committed now instead of waiting for the
    /// next boot's <see cref="CatchUp"/> to re-discover it.
    /// </summary>
    public IReadOnlyList<WorkspaceEvidenceFlushResult> FlushAll()
    {
        List<KeyValuePair<string, PendingRepo>> all;
        lock (_lock)
        {
            if (_pending.Count == 0) return Array.Empty<WorkspaceEvidenceFlushResult>();
            all = _pending.ToList();
            _pending.Clear();
        }
        return CommitBuckets(all);
    }

    private IReadOnlyList<WorkspaceEvidenceFlushResult> CommitBuckets(
        List<KeyValuePair<string, PendingRepo>> buckets)
    {
        if (buckets.Count == 0) return Array.Empty<WorkspaceEvidenceFlushResult>();

        var results = new List<WorkspaceEvidenceFlushResult>(buckets.Count);
        foreach (var kv in buckets)
        {
            var repo = kv.Value;
            var result = _commit.TryCommitEvidence(
                kv.Key, repo.WatchPaths.ToList(), ExcludeGlobs, BuildMessage(repo.Count, repo.Items));
            MaybeEnqueuePush(kv.Key, result, "evidence");
            results.Add(new WorkspaceEvidenceFlushResult(kv.Key, repo.Count, result));
        }
        return results;
    }

    /// <summary>
    /// Boot catch-up: commit any drift that accumulated while the backend was
    /// down, once, across every distinct repo behind the supplied watch paths.
    /// </summary>
    public IReadOnlyList<WorkspaceEvidenceFlushResult> CatchUp(IEnumerable<string> watchPaths)
    {
        var byRoot = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var wp in watchPaths)
        {
            if (string.IsNullOrWhiteSpace(wp)) continue;
            string? root;
            try { root = _commit.ResolveWorkspaceGitRoot(wp); }
            catch { continue; }
            if (root == null) continue;
            if (!IsTaskRepoRoot(root))
            {
                _logger.LogDebug(
                    "workspace-evidence catch-up skipped: {WatchPath} resolves to {GitRoot}, outside the task repository", wp, root);
                continue;
            }
            if (!byRoot.TryGetValue(root, out var list))
                byRoot[root] = list = new List<string>();
            list.Add(wp);
        }

        if (byRoot.Count == 0) return Array.Empty<WorkspaceEvidenceFlushResult>();

        var results = new List<WorkspaceEvidenceFlushResult>(byRoot.Count);
        foreach (var kv in byRoot)
        {
            var result = _commit.TryCommitEvidence(
                kv.Key, kv.Value, ExcludeGlobs, "evidence: catch-up nach neustart\n");
            MaybeEnqueuePush(kv.Key, result, "evidence-catchup");
            results.Add(new WorkspaceEvidenceFlushResult(kv.Key, 0, result));
        }
        return results;
    }

    /// <summary>
    /// True when <paramref name="gitRoot"/> is the configured TaskRepository's
    /// git root — the only repo evidence may be committed into. Returns true
    /// unconditionally when TaskRepository is unset (guard off), so the batcher
    /// still works as a primitive without a configured workspace.
    /// </summary>
    private bool IsTaskRepoRoot(string gitRoot)
    {
        var taskRepoRoot = TaskRepoGitRoot();
        return taskRepoRoot == null
            || string.Equals(gitRoot, taskRepoRoot, StringComparison.OrdinalIgnoreCase);
    }

    private string? TaskRepoGitRoot()
    {
        if (_taskRepoRootResolved) return _taskRepoRoot;
        lock (_rootLock)
        {
            if (_taskRepoRootResolved) return _taskRepoRoot;
            var configured = _config["TaskRepository"];
            try
            {
                _taskRepoRoot = string.IsNullOrWhiteSpace(configured)
                    ? null
                    : _commit.ResolveWorkspaceGitRoot(configured);
            }
            catch { _taskRepoRoot = null; }
            _taskRepoRootResolved = true;
            return _taskRepoRoot;
        }
    }

    private void MaybeEnqueuePush(string gitRoot, WorkspaceArtifactCommitResult result, string label)
    {
        if (!result.DidCommit || !PushEnabled || _push == null) return;
        if (!_push.Enqueue(new WorkspaceArtifactPushRequest(gitRoot, label)))
            _logger.LogWarning("workspace-evidence-push enqueue failed repo={Repo}", gitRoot);
    }

    internal static string BuildMessage(int transitionCount, IReadOnlyList<(string project, string slug)> items)
    {
        var byProject = items
            .Where(i => !string.IsNullOrWhiteSpace(i.slug))
            .GroupBy(i => string.IsNullOrWhiteSpace(i.project) ? "?" : i.project.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        var parts = new List<string>();
        foreach (var group in byProject)
        {
            var slugs = group.Select(x => x.slug.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var shown = slugs.Take(8).ToList();
            var extra = slugs.Count - shown.Count;
            var slugText = string.Join(",", shown) + (extra > 0 ? $",+{extra}" : string.Empty);
            parts.Add($"{group.Key}: {slugText}");
        }

        var compact = parts.Count > 0 ? string.Join("; ", parts) : "(no-slugs)";
        if (compact.Length > 160) compact = compact[..157] + "...";
        var label = transitionCount == 1 ? "transition" : "transitions";
        return $"evidence: {transitionCount} {label} - {compact}\n";
    }

    private sealed class PendingRepo
    {
        public DateTimeOffset First;
        public DateTimeOffset Last;
        public int Count;
        public readonly HashSet<string> WatchPaths = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(string project, string slug)> Items = new();

        public PendingRepo(DateTimeOffset now)
        {
            First = now;
            Last = now;
        }
    }
}

/// <summary>Outcome of one repo's evidence flush: the git root, how many
/// transitions it aggregated, and the underlying commit result.</summary>
public sealed record WorkspaceEvidenceFlushResult(
    string GitRoot,
    int TransitionCount,
    WorkspaceArtifactCommitResult Result);
