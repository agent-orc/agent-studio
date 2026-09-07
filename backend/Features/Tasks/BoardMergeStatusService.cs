using System.Collections.Concurrent;

namespace AgentStudio.Tasks;

/// <summary>
/// Builds the board's always-on merge signal (AGT-2046): for every card, is the
/// task's work folded into the integration branch (develop) and/or the release
/// branch (main)? The result is attached to <see cref="TaskInfo.MergeSignal"/> so
/// the kanban card renders a two-segment <c>[develop|main]</c> indicator.
///
/// <para>
/// The design invariant is <b>no per-card git spawn</b>. Instead of a
/// <c>merge-base --is-ancestor</c> fork per card (which would be a spawn orgy on a
/// 200-card board), the service computes ONE pair of reachability sets per
/// repository - the full ancestor SHA sets of develop and main
/// (<see cref="GitService.GetAncestorShaSet"/>) - and answers membership for every
/// card in that repo with an in-memory hash lookup. That is O(repos) git spawns,
/// matching the batched read strategy from AGT-2007 (the provenance view) and
/// AGT-2013 (git-info caching). The per-repo sets are cached for a short TTL so a
/// polling board reuses them across requests.
/// </para>
///
/// <para>
/// The card source is the same attributed commit set used by
/// <see cref="TaskIntegrationStatusService"/>. Every attributed commit must be
/// present in a target branch for that segment to light up. Lane state,
/// provenance merge records, and branch tips never override graph membership.
/// This keeps the compact chip aligned with the canonical integration field.
/// </para>
/// </summary>
public sealed class BoardMergeStatusService
{
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly ILogger<BoardMergeStatusService> _logger;
    private readonly TaskIntegrationStatusService? _integrationStatus;

    public const string ReleaseBranch = "main";

    /// <summary>
    /// Safety lifetime for a repository's reachability sets. Normal invalidation
    /// is ref-driven, so stable repositories reuse one projection across board
    /// polls while branch moves become visible immediately.
    /// </summary>
    // Ref fingerprints invalidate immediately when develop/main moves. The long
    // TTL is only a safety refresh for unusual git layouts the fingerprint
    // cannot observe, not the normal board-poll invalidation mechanism.
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan ShortFallbackTtl = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan FailureCacheTtl = TimeSpan.FromSeconds(1);

    private readonly GenerationSingleFlightCache<RepoReachability> _cache;
    private int _computationCount;

    public BoardMergeStatusService(
        GitService git,
        ProjectSettingsService settings,
        ILogger<BoardMergeStatusService> logger,
        TaskIntegrationStatusService? integrationStatus = null)
        : this(git, settings, logger, TimeProvider.System, integrationStatus)
    {
    }

    internal BoardMergeStatusService(
        GitService git,
        ProjectSettingsService settings,
        ILogger<BoardMergeStatusService> logger,
        TimeProvider timeProvider,
        TaskIntegrationStatusService? integrationStatus = null)
    {
        _git = git;
        _settings = settings;
        _logger = logger;
        _integrationStatus = integrationStatus;
        _cache = new GenerationSingleFlightCache<RepoReachability>(timeProvider);
    }

    /// <summary>
    /// Per-<see cref="TaskInfo.TaskKey"/> merge signal for the given board. Only
    /// cards with an attributed task commit (see <see cref="AnchorFor"/>) get an
    /// entry - a card with nothing committed carries no signal and the card renders
    /// none. Never throws: a git failure yields the conservative "not merged"
    /// reading for that repo.
    /// </summary>
    public Dictionary<string, TaskMergeSignal> BuildLookup(IReadOnlyCollection<TaskInfo> jobs)
    {
        var result = new Dictionary<string, TaskMergeSignal>(StringComparer.Ordinal);
        if (jobs.Count == 0) return result;

        using var _t = GitProcessTelemetry.BeginRequest("board/merge-status", _logger);

        // Group the anchored cards by resolved repo root so the batched ancestor
        // sets are computed ONCE per repository, not per card.
        var byRepo = new Dictionary<RepoBranchKey, List<TaskInfo>>();
        foreach (var job in jobs)
        {
            if (TaskIntegrationStatusService.AttributedCommits(job).Count == 0) continue;
            var root = _git.ResolveRepoRootForWatchPath(job.WatchPath);
            if (string.IsNullOrWhiteSpace(root)) continue;
            var key = new RepoBranchKey(root!, ConfiguredIntegrationBranch(job));
            if (!byRepo.TryGetValue(key, out var list))
            {
                list = new List<TaskInfo>();
                byRepo[key] = list;
            }
            list.Add(job);
        }

        var reaches = new ConcurrentDictionary<RepoBranchKey, RepoReachability>();
        Parallel.ForEach(
            byRepo,
            new ParallelOptions { MaxDegreeOfParallelism = ReadOnlyGitConcurrencyLimiter.MaxConcurrency },
            pair =>
            {
                reaches[pair.Key] = GetReachability(pair.Key);
            });

        foreach (var (repoBranch, repoJobs) in byRepo)
        {
            var reach = reaches[repoBranch];

            foreach (var job in repoJobs)
            {
                var commits = TaskIntegrationStatusService.AttributedCommits(job);
                var anchor = commits[^1];
                var branch = !string.IsNullOrWhiteSpace(job.Provenance?.Branch)
                    ? job.Provenance!.Branch
                    : WorktreeTaskLifecycle.BranchFor(job.Id);

                var inIntegration = commits.All(sha =>
                    TaskIntegrationStatusService.AncestorSetContains(reach.Integration, sha));
                var inRelease = commits.All(sha =>
                    TaskIntegrationStatusService.AncestorSetContains(reach.Release, sha));

                result[job.TaskKey] = new TaskMergeSignal
                {
                    Branch = branch,
                    InIntegration = inIntegration,
                    InRelease = inRelease,
                    IntegrationBranch = reach.IntegrationBranch,
                    ReleaseBranch = ReleaseBranch,
                    IntegrationSha = inIntegration ? Short(anchor) : null,
                    ReleaseSha = inRelease ? Short(anchor) : null,
                };
            }
        }

        // Multi-repository and direct-to-branch deliveries have no single task
        // branch graph. Reuse the canonical repository projection and collapse
        // every repository entry into the compact card signal.
        if (_integrationStatus is not null)
        {
            var multiRepositoryJobs = jobs.Where(job =>
                    TaskIntegrationStatusService.AttributedCommitRecords(job)
                        .Select(commit => commit.Repository ?? string.Empty)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Skip(1)
                        .Any())
                .ToList();
            var repositoryStatuses = _integrationStatus.BuildLookup(multiRepositoryJobs);
            foreach (var job in multiRepositoryJobs)
            {
                if (!repositoryStatuses.TryGetValue(job.TaskKey, out var status)
                    || status.Repositories.Count == 0)
                    continue;
                var commits = TaskIntegrationStatusService.AttributedCommitRecords(job);
                if (commits.Count == 0) continue;
                var anchor = status.Repositories.SelectMany(repository => repository.Commits).Last().Sha;
                var inIntegration = status.Repositories.All(repository => repository.OnIntegrationBranch);
                var inRelease = status.Repositories.All(repository => repository.OnReleaseBranch);
                result[job.TaskKey] = new TaskMergeSignal
                {
                    Branch = TaskIntegrationStatusService.DeliveryRefFor(job)
                        ?? commits.LastOrDefault(commit => !string.IsNullOrWhiteSpace(commit.Branch))?.Branch
                        ?? string.Empty,
                    InIntegration = inIntegration,
                    InRelease = inRelease,
                    IntegrationBranch = status.IntegrationBranch,
                    ReleaseBranch = ReleaseBranch,
                    IntegrationSha = inIntegration ? Short(anchor) : null,
                    ReleaseSha = inRelease ? Short(anchor) : null,
                };
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves develop/main presence for an arbitrary bounded commit set using
    /// the exact same ref-fingerprinted reachability projection as
    /// <see cref="BuildLookup"/>. The Project Hub Git graph calls this once per
    /// history page, then renders every commit from in-memory set membership.
    /// This is intentionally the shared resolver, not a second ancestry path.
    /// </summary>
    public Dictionary<string, CommitBranchPresence> BuildCommitPresence(
        string projectName,
        string repoRoot,
        IEnumerable<string> commitShas)
    {
        var shas = commitShas
            .Where(ReviewSubjectStore.IsValidResultSha)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (shas.Count == 0 || string.IsNullOrWhiteSpace(repoRoot)) return [];

        using var _t = GitProcessTelemetry.BeginRequest("git/commit-presence", _logger);
        var configured = _settings.Get(projectName).IntegrationBranch;
        var reach = GetReachability(new RepoBranchKey(repoRoot, configured));
        return shas.ToDictionary(
            sha => sha,
            sha => new CommitBranchPresence(
                reach.Integration.Contains(sha),
                reach.Release.Contains(sha),
                reach.IntegrationBranch,
                ReleaseBranch),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The latest integrable attributed task commit. This delegates to the
    /// canonical integration source so zero-file lifecycle markers, legacy
    /// singular commits, and abbreviated SHA handling stay aligned.
    /// </summary>
    internal static string? AnchorFor(TaskInfo job)
        => TaskIntegrationStatusService.AnchorFor(job);

    /// <summary>
    /// The develop + main ancestor SHA sets for one repo. TWO (up to four with the
    /// <c>origin/</c> mirror) <c>rev-list</c> spawns per repo per TTL window. Repos
    /// are processed with bounded parallelism while each repo's reads stay
    /// sequential, avoiding the former unbounded <c>Task.Run</c> fan-out. Both
    /// the local ref and its <c>origin/</c> mirror are unioned so a fresh clone with
    /// only remote-tracking branches still resolves, matching
    /// <see cref="TaskProvenanceService"/>'s local-or-origin semantics.
    /// </summary>
    private RepoReachability ComputeReachability(string root, string configuredBranch)
    {
        Interlocked.Increment(ref _computationCount);
        return ReadOnlyGitConcurrencyLimiter.Run(() =>
        {
            // Branch resolution is part of the cached computation. Previously
            // every board request resolved it before checking the reachability
            // cache, which still spawned git processes on an otherwise-hot hit.
            var integrationRef = _git.ResolveIntegrationReadRef(root, configuredBranch);
            var integrationBranch = integrationRef.StartsWith("origin/", StringComparison.Ordinal)
                ? integrationRef["origin/".Length..]
                : integrationRef;
            var integrationSucceeded = _git.TryGetAncestorShaSet(
                root,
                [integrationBranch, "origin/" + integrationBranch],
                out var integration);
            var releaseSucceeded = _git.TryGetAncestorShaSet(
                root,
                [ReleaseBranch, "origin/" + ReleaseBranch],
                out var release);
            return new RepoReachability(
                integrationBranch,
                integration,
                release,
                integrationSucceeded && releaseSucceeded);
        });
    }

    private RepoReachability GetReachability(RepoBranchKey key)
    {
        var cacheKey = $"{key.Root}\0{key.Branch}";
        var refFingerprint = ReadOnlyGitRefFingerprint.CaptureDetailed(
            key.Root,
            [key.Branch, ReleaseBranch]);
        return _cache.GetOrCreateVersioned(
            cacheKey,
            refFingerprint.Value,
            value => value.Succeeded
                ? refFingerprint.RequiresShortFallback ? ShortFallbackTtl : CacheTtl
                : FailureCacheTtl,
            () => ComputeReachability(key.Root, key.Branch));
    }

    private string ConfiguredIntegrationBranch(TaskInfo task)
    {
        return _settings.Get(task.ProjectName).IntegrationBranch;
    }

    /// <summary>Drops the cached reachability sets. Tests use this to force a fresh read.</summary>
    internal void InvalidateCache() => _cache.Invalidate();

    internal int ComputationCount => Volatile.Read(ref _computationCount);

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private sealed record RepoReachability(
        string IntegrationBranch,
        HashSet<string> Integration,
        HashSet<string> Release,
        bool Succeeded);

    private sealed record RepoBranchKey(string Root, string Branch);
}

public sealed record CommitBranchPresence(
    bool InIntegration,
    bool InRelease,
    string IntegrationBranch,
    string ReleaseBranch);
