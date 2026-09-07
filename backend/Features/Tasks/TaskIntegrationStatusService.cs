using System.Collections.Concurrent;
using AgentStudio.Registry;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tasks;

/// <summary>
/// AGT-2202 - computes the honest, git-derived integration verdict for delivered
/// cards (4-auto-review / 5-human-review / 5e-escalated / 6-completed / 7-archive): is the task's
/// work actually folded into the integration branch (develop)? The result is attached to
/// <see cref="TaskInfo.Integration"/> so the board renders a single, unambiguous
/// "integrated / not integrated / conflict / no branch" badge on every delivered
/// card, and so acceptance can refuse a delivery that has not passed the
/// integration boundary.
///
/// <para>
/// The verdict's anchor is the current immutable review result when one exists,
/// otherwise the integrable subset of the attributed <c>commits[]</c> list.
/// Zero-file runner lifecycle markers are not delivery expectations; every
/// commit that carries changed files remains an anchor within its current
/// review generation. This
/// keeps the badge aligned with delivered work (AGT-2171: the widget showed the
/// attributed commits on develop while the badge, keying off the branch
/// <em>tip</em> WIP snapshot, claimed "not integrated"). The signals are collapsed
/// into the five
/// <see cref="IntegrationStatuses"/> states:
/// <list type="number">
/// <item>ALL attributed commits are ancestors of develop → <c>integrated</c> (even
///   when the branch tip still carries further, un-integrated WIP commits the
///   widget never showed);</item>
/// <item>SOME attributed commits are ancestors → <c>partial</c>, with the missing
///   short-SHAs in the detail;</item>
/// <item>NONE are ancestors → <c>pending</c> (or <c>conflict-skipped</c> when a
///   typed accepted-integration failure was recorded);</item>
/// <item>no attributed commit and no evidenced delivery ref →
///   <c>no-branch</c>.</item>
/// </list>
/// </para>
///
/// <para>
/// Same design invariant as <see cref="BoardMergeStatusService"/>: <b>no per-card
/// git spawn on the hot path</b>. Per repository it computes one target-branch
/// ancestor SHA set, cached against the resolved target HEAD fingerprint, and
/// answers every card in that repo with in-memory lookups. Provenance merge
/// records, pipeline success, lane state, and curated merge subjects never
/// override commit membership. A current review subject is an ancestry fallback
/// only when no attributed commit exists; it cannot hide a missing current
/// commit. This also detects out-of-band merges on the next read. Per-card local
/// reads resolve the delivery ref from the same task card
/// and review-subject truth as acceptance; the not-integrated subset also reads
/// <c>pipeline-execution.json</c> best-effort (integration-failed vs. plain
/// pending). Never throws: a git failure yields the conservative reading.
/// </para>
/// </summary>
public sealed class TaskIntegrationStatusService
{
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly PipelineExecutionLog _pipelineLog;
    private readonly ILogger<TaskIntegrationStatusService> _logger;
    private readonly ProjectRegistry? _registry;

    /// <summary>The delivered lanes this verdict applies to. Cards outside get no entry.</summary>
    internal static readonly HashSet<string> DeliveredLanes = new(StringComparer.Ordinal)
    {
        TaskStates.AutoReview,
        TaskStates.Escalated,
        TaskStates.HumanReview,
        TaskStates.Completed,
        TaskStates.Archive,
    };

    internal static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan ShortFallbackTtl = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan FailureCacheTtl = TimeSpan.FromSeconds(1);

    private readonly GenerationSingleFlightCache<RepoIntegration> _cache;
    private int _computationCount;

    public TaskIntegrationStatusService(
        GitService git,
        ProjectSettingsService settings,
        PipelineExecutionLog pipelineLog,
        ILogger<TaskIntegrationStatusService> logger,
        ProjectRegistry? registry = null)
        : this(git, settings, pipelineLog, logger, TimeProvider.System, registry)
    {
    }

    internal TaskIntegrationStatusService(
        GitService git,
        ProjectSettingsService settings,
        PipelineExecutionLog pipelineLog,
        ILogger<TaskIntegrationStatusService> logger,
        TimeProvider timeProvider,
        ProjectRegistry? registry = null)
    {
        _git = git;
        _settings = settings;
        _pipelineLog = pipelineLog;
        _logger = logger;
        _registry = registry;
        _cache = new GenerationSingleFlightCache<RepoIntegration>(timeProvider);
    }

    /// <summary>
    /// Per-<see cref="TaskInfo.TaskKey"/> integration verdict for delivered cards
    /// in the given board set. Auto Review is included because a green Remote
    /// delivery now integrates before it moves to Human Review. Every earlier
    /// lane carries no verdict and the card renders none. Never throws.
    /// </summary>
    public Dictionary<string, TaskIntegrationStatus> BuildLookup(IReadOnlyCollection<TaskInfo> jobs)
    {
        var result = new Dictionary<string, TaskIntegrationStatus>(StringComparer.Ordinal);
        if (jobs.Count == 0) return result;

        using var _t = GitProcessTelemetry.BeginRequest("board/integration-status", _logger);

        var work = new Dictionary<TaskInfo, List<RepositoryCommitGroup>>();
        var repoKeys = new HashSet<RepoBranchKey>();
        foreach (var job in jobs.Where(job => DeliveredLanes.Contains(job.State)))
        {
            var groups = BuildRepositoryGroups(job);
            work[job] = groups;
            foreach (var group in groups)
                if (group.Key is not null) repoKeys.Add(group.Key);
        }

        var reaches = new ConcurrentDictionary<RepoBranchKey, RepoIntegration>();
        Parallel.ForEach(
            repoKeys,
            new ParallelOptions { MaxDegreeOfParallelism = ReadOnlyGitConcurrencyLimiter.MaxConcurrency },
            key =>
            {
                var cacheKey = $"{key.Root}\0{key.Branch}";
                var refFingerprint = ReadOnlyGitRefFingerprint.CaptureDetailed(
                    key.Root,
                    [key.Branch, BoardMergeStatusService.ReleaseBranch]);
                reaches[key] = _cache.GetOrCreateVersioned(
                    cacheKey,
                    refFingerprint.Value,
                    value => value.Succeeded
                        ? refFingerprint.RequiresShortFallback ? ShortFallbackTtl : CacheTtl
                        : FailureCacheTtl,
                    () => ComputeRepoIntegration(key.Root, key.Branch));
            });

        foreach (var (job, groups) in work)
        {
            result[job.TaskKey] = ClassifyRepositories(job, groups, reaches);
        }

        return result;
    }

    /// <summary>
    /// Returns whether the exact fenced remote delivery reviewed for this task is
    /// already an ancestor of the configured integration branch. This is a
    /// recovery-only distinction: the attributed commit set can be present while
    /// a later fenced lifecycle snapshot is not itself an integration
    /// expectation. A process crash after the local merge can also leave the
    /// exact result SHA contained while losing the pipeline record and queued
    /// push.
    /// </summary>
    public bool IsFencedDeliveryIntegrated(TaskInfo job)
    {
        try
        {
            var subject = ReviewSubjectStore.Read(job.FolderPath);
            if (subject is null || !ReviewSubjectStore.IsValidResultSha(subject.ResultSha))
                return false;

            var root = _git.ResolveRepoRootForWatchPath(job.WatchPath);
            if (string.IsNullOrWhiteSpace(root))
                return false;

            var branch = _git.ResolveIntegrationBranch(
                root,
                ConfiguredIntegrationBranch(job));
            return _git.IsAncestor(root, subject.ResultSha, branch);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "TaskIntegrationStatusService: fenced delivery ancestry is best-effort");
            return false;
        }
    }

    /// <summary>
    /// Resolves accepted-integration recovery from the same Git-derived status
    /// projected onto the board. Pipeline history describes the last attempt;
    /// it cannot turn a delivery missing from the target branch into an
    /// integrated delivery.
    /// </summary>
    internal AcceptedIntegrationRecoveryDecision ResolveAcceptedIntegrationRecovery(
        TaskInfo job,
        TaskIntegrationStatus? status)
    {
        var lastMerge = ReadLatestMergeStep(job);
        if (!AcceptanceIntegrationPolicy.IsIntegrationRequired(job)
            || string.Equals(lastMerge?.Verdict, "operator-override", StringComparison.OrdinalIgnoreCase))
        {
            return new AcceptedIntegrationRecoveryDecision(
                AcceptedIntegrationRecoveryAction.Ignore,
                "This acceptance explicitly expects no integration.",
                lastMerge);
        }
        if (status?.Status == IntegrationStatuses.Integrated
            && lastMerge?.Status != PipelineStepStatus.Pending)
        {
            return new AcceptedIntegrationRecoveryDecision(
                AcceptedIntegrationRecoveryAction.Finalize,
                "Git proves that the attributed delivery is integrated; no merge replay is required.",
                lastMerge);
        }

        // AGT-2688: the merge itself already succeeded and only the deferred
        // push is blocked (main/develop lineage, or a diverged remote). That is
        // not a merge-replay case - re-running the merge cannot fix a push
        // problem, and doing so on every backstop sweep is exactly the loop
        // that burned the window overnight. The integration push backstop owns
        // retrying the push; this recovery path must leave the card alone.
        if (lastMerge?.Status == PipelineStepStatus.Passed
            && status?.Status == IntegrationStatuses.ConflictSkipped
            && status.Failure?.Code == AcceptedIntegrationFailureCodes.IntegrationPushBlocked)
        {
            return new AcceptedIntegrationRecoveryDecision(
                AcceptedIntegrationRecoveryAction.Ignore,
                "The merge already succeeded; only the deferred push is blocked and is retried by the integration push backstop.",
                lastMerge);
        }

        // BP-02: a crash can leave the merge commit in local ancestry while the
        // exact-SHA gate verdict is still pending. That state must resume the
        // runner rather than treating ancestry as proof that the gate ran.

        if (IsDecidedIntegrationAttempt(lastMerge))
        {
            return new AcceptedIntegrationRecoveryDecision(
                AcceptedIntegrationRecoveryAction.ReturnToReview,
                "The latest integration attempt requires an operator or steer round.",
                lastMerge);
        }

        return new AcceptedIntegrationRecoveryDecision(
            AcceptedIntegrationRecoveryAction.Retry,
            lastMerge?.Status == PipelineStepStatus.Passed
                ? "The Passed step contradicts current Git truth and must be revalidated."
                : "The accepted integration has no terminal recovery decision.",
            lastMerge);
    }

    /// <summary>
    /// The verdict for one card given its repo's cached integration facts. A
    /// verdict is derived entirely from the target-branch ancestry of the card's attributed
    /// <c>commits[]</c> (the same list the commit widget shows): all landed →
    /// integrated, some → partial, none → pending/conflict, and no attributed
    /// commit plus no delivery ref → no-branch. The branch tip is deliberately
    /// NOT an ancestry anchor - it is a WIP snapshot the widget never shows,
    /// whose use was the AGT-2171 badge/widget self-contradiction. It remains
    /// valid evidence that a local delivery ref exists.
    /// </summary>
    private TaskIntegrationStatus ClassifyRepositories(
        TaskInfo job,
        IReadOnlyList<RepositoryCommitGroup> groups,
        IReadOnlyDictionary<RepoBranchKey, RepoIntegration> reaches)
    {
        var primaryBranch = ConfiguredIntegrationBranch(job);
        if (groups.Count == 0)
        {
            var primaryRoot = _git.ResolveRepoRootForWatchPath(job.WatchPath);
            if (string.IsNullOrWhiteSpace(primaryRoot))
                return ClassifyNotIntegrated(job, primaryBranch);
            var primaryKey = new RepoBranchKey(primaryRoot, primaryBranch);
            return reaches.TryGetValue(primaryKey, out var primaryReach)
                ? ClassifyWithRepo(job, primaryReach)
                : ClassifyNotIntegrated(job, primaryBranch);
        }

        var repositoryEntries = new List<TaskRepositoryIntegrationStatus>(groups.Count);
        foreach (var group in groups)
        {
            var reach = group.Key is not null && reaches.TryGetValue(group.Key, out var found)
                ? found
                : null;
            var effectiveCommits = group.Commits.Where(commit =>
                !IsZeroFileLifecycleMarker(commit)
                || reach is not null && AncestorSetContains(reach.DevelopAncestors, commit.Sha));
            var memberships = effectiveCommits.Select(commit => new TaskRepositoryCommitMembership
            {
                Sha = commit.Sha,
                OnIntegrationBranch = reach is not null
                    && AncestorSetContains(reach.DevelopAncestors, commit.Sha),
                OnReleaseBranch = reach is not null
                    && AncestorSetContains(reach.ReleaseAncestors, commit.Sha),
            }).ToList();
            if (memberships.Count == 0) continue;
            var integratedCount = memberships.Count(commit => commit.OnIntegrationBranch);
            var releasedCount = memberships.Count(commit => commit.OnReleaseBranch);
            var missing = memberships.Where(commit => !commit.OnIntegrationBranch)
                .Select(commit => Short(commit.Sha))
                .ToList();
            var branch = reach?.IntegrationBranch ?? group.IntegrationBranch;
            var detail = reach is null
                ? $"Repository checkout is unavailable; {memberships.Count} commit(s) could not be evaluated."
                : missing.Count == 0
                    ? releasedCount == memberships.Count
                        ? $"{integratedCount}/{memberships.Count} on {branch} and {BoardMergeStatusService.ReleaseBranch}."
                        : $"{integratedCount}/{memberships.Count} on {branch}; {releasedCount}/{memberships.Count} on {BoardMergeStatusService.ReleaseBranch}."
                    : $"{integratedCount}/{memberships.Count} on {branch}; missing: {string.Join(", ", missing)}.";
            repositoryEntries.Add(new TaskRepositoryIntegrationStatus
            {
                Repository = group.Repository,
                Commits = memberships,
                IntegrationBranch = branch,
                ReleaseBranch = BoardMergeStatusService.ReleaseBranch,
                OnIntegrationBranch = memberships.Count > 0 && integratedCount == memberships.Count,
                OnReleaseBranch = memberships.Count > 0 && releasedCount == memberships.Count,
                Detail = detail,
            });
        }

        var projectedBranch = repositoryEntries.FirstOrDefault()?.IntegrationBranch ?? primaryBranch;
        var total = repositoryEntries.Sum(entry => entry.Commits.Count);
        var integrated = repositoryEntries.Sum(entry =>
            entry.Commits.Count(commit => commit.OnIntegrationBranch));
        var allIntegrated = repositoryEntries.Count > 0
            && repositoryEntries.All(entry => entry.OnIntegrationBranch);
        if (allIntegrated)
        {
            var anchor = repositoryEntries.SelectMany(entry => entry.Commits).Last().Sha;
            return Integrated(
                Short(anchor),
                projectedBranch,
                DeliveryRefFor(job),
                "anchor-ancestor") with { Repositories = repositoryEntries };
        }

        if (integrated > 0)
        {
            var missing = repositoryEntries
                .Where(entry => !entry.OnIntegrationBranch)
                .Select(entry => $"{entry.Repository}: {string.Join(", ", entry.Commits.Where(commit => !commit.OnIntegrationBranch).Select(commit => Short(commit.Sha)))}");
            return new TaskIntegrationStatus
            {
                Status = IntegrationStatuses.Partial,
                DeliveryRef = DeliveryRefFor(job),
                IntegrationBranch = projectedBranch,
                Detail = $"{integrated}/{total} attributed commits integrated; missing by repository: {string.Join("; ", missing)}",
                Repositories = repositoryEntries,
            };
        }

        return ClassifyNotIntegrated(job, projectedBranch, repositories: repositoryEntries);
    }

    private List<RepositoryCommitGroup> BuildRepositoryGroups(TaskInfo job)
    {
        var commits = AttributedCommitRecords(job);
        if (commits.Count == 0) return [];

        var primaryRoot = _git.ResolveRepoRootForWatchPath(job.WatchPath);
        var primaryOrigin = string.IsNullOrWhiteSpace(primaryRoot)
            ? null
            : _git.ReadOriginUrlAt(primaryRoot);
        IReadOnlyList<ProjectRecord> registeredProjects = [];
        try
        {
            registeredProjects = _registry?.List() ?? [];
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "TaskIntegrationStatusService: registry repository lookup is best-effort");
        }
        return commits
            .GroupBy(
                commit => CanonicalRepositoryIdentity(
                    RepositoryIdentity(commit, primaryOrigin),
                    registeredProjects),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => ResolveRepositoryGroup(
                job,
                group.Key,
                group.ToList(),
                primaryRoot,
                primaryOrigin,
                registeredProjects))
            .ToList();
    }

    private RepositoryCommitGroup ResolveRepositoryGroup(
        TaskInfo job,
        string repository,
        List<TaskCommitInfo> commits,
        string? primaryRoot,
        string? primaryOrigin,
        IReadOnlyList<ProjectRecord> registeredProjects)
    {
        var registered = registeredProjects.FirstOrDefault(project => RepositoryAliases(project)
            .Contains(repository, StringComparer.OrdinalIgnoreCase));

        if (registered is not null)
        {
            var root = FirstExistingDirectory(registered.RepositoryPath, registered.RootPath);
            var branch = _settings.Get(registered.DisplayName).IntegrationBranch;
            return new RepositoryCommitGroup(
                RegisteredRepositoryLabel(registered),
                commits,
                root is null ? null : new RepoBranchKey(root, branch),
                branch);
        }

        if (!string.IsNullOrWhiteSpace(primaryRoot)
            && (string.IsNullOrWhiteSpace(commits[0].Repository)
                || SameRepository(repository, primaryOrigin)
                || string.Equals(repository, RepositoryLabel(primaryOrigin, primaryRoot), StringComparison.OrdinalIgnoreCase)))
        {
            var branch = ConfiguredIntegrationBranch(job);
            return new RepositoryCommitGroup(
                RepositoryLabel(repository, primaryRoot),
                commits,
                new RepoBranchKey(primaryRoot, branch),
                branch);
        }

        if (Directory.Exists(repository))
        {
            var branch = commits.Select(commit => commit.Branch)
                .FirstOrDefault(value => string.Equals(value, "develop", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "main", StringComparison.OrdinalIgnoreCase))
                ?? "main";
            return new RepositoryCommitGroup(
                RepositoryLabel(repository, repository),
                commits,
                new RepoBranchKey(repository, branch),
                branch);
        }

        return new RepositoryCommitGroup(
            RepositoryLabel(repository, repository),
            commits,
            null,
            commits.Select(commit => commit.Branch).FirstOrDefault(branch => branch is "develop" or "main") ?? "main");
    }

    private static IReadOnlyList<string> RepositoryAliases(ProjectRecord project)
    {
        var values = new List<string> { project.Id, project.DisplayName };
        if (!string.IsNullOrWhiteSpace(project.RepositoryPath))
        {
            values.Add(project.RepositoryPath);
            values.Add(RepositoryLabel(project.RepositoryPath, project.RepositoryPath));
        }
        foreach (var url in project.Urls.Where(url =>
                     string.Equals(url.Id, "repo", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(url.Label, "repository", StringComparison.OrdinalIgnoreCase)))
        {
            values.Add(url.Url);
            values.Add(RepositoryLabel(url.Url, url.Url));
            var id = RepositoryIdentityContract.FromUrl(url.Url);
            if (id is not null) values.Add(id);
        }
        return values;
    }

    private static string CanonicalRepositoryIdentity(
        string repository,
        IReadOnlyList<ProjectRecord> registeredProjects)
    {
        var repositoryId = RepositoryIdentityContract.FromUrl(repository);
        var registered = registeredProjects.FirstOrDefault(project =>
        {
            var aliases = RepositoryAliases(project);
            return aliases.Contains(repository, StringComparer.OrdinalIgnoreCase)
                || (repositoryId is not null
                    && aliases.Contains(repositoryId, StringComparer.OrdinalIgnoreCase));
        });
        return registered?.Id ?? repository.Trim().TrimEnd('/', '\\');
    }

    private static string RepositoryIdentity(TaskCommitInfo commit, string? primaryOrigin)
        => commit.Repository?.Trim()
            ?? primaryOrigin?.Trim()
            ?? "repository";

    private static bool SameRepository(string value, string? other)
        => !string.IsNullOrWhiteSpace(other)
            && (string.Equals(value.Trim().TrimEnd('/', '\\'), other.Trim().TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase)
                || string.Equals(RepositoryIdentityContract.FromUrl(value), RepositoryIdentityContract.FromUrl(other), StringComparison.Ordinal));

    private static string RepositoryLabel(string? identity, string? fallback)
    {
        var source = string.IsNullOrWhiteSpace(identity) ? fallback : identity;
        if (string.IsNullOrWhiteSpace(source)) return "repository";
        var trimmed = source.Trim().TrimEnd('/', '\\');
        var separator = Math.Max(trimmed.LastIndexOf('/'), Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf(':')));
        var label = separator >= 0 ? trimmed[(separator + 1)..] : trimmed;
        return label.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? label[..^4] : label;
    }

    private static string RegisteredRepositoryLabel(ProjectRecord project)
    {
        var repositoryUrl = project.Urls.FirstOrDefault(url =>
            string.Equals(url.Id, "repo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(url.Label, "repository", StringComparison.OrdinalIgnoreCase))?.Url;
        return RepositoryLabel(repositoryUrl, project.RepositoryPath ?? project.DisplayName);
    }

    private static string? FirstExistingDirectory(params string?[] candidates)
        => candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate));

    private TaskIntegrationStatus ClassifyWithRepo(TaskInfo job, RepoIntegration reach)
    {
        var branchName = reach.IntegrationBranch;
        var deliveryRef = DeliveryRefFor(job);

        // Anchor = integrable entries in the attributed commits[] list. Zero-file
        // runner lifecycle markers are metadata, not delivery expectations.
        // A current review subject is only a fallback when attribution is empty;
        // it must never hide a genuine missing, non-superseded commit.
        var attributed = AttributedCommits(job, reach.DevelopAncestors);
        var reviewedResultSha = ReviewSubjectStore.Read(job.FolderPath)?.ResultSha;
        if (attributed.Count == 0
            && !string.IsNullOrWhiteSpace(reviewedResultSha)
            && AncestorSetContains(reach.DevelopAncestors, reviewedResultSha))
        {
            return Integrated(
                Short(reviewedResultSha),
                branchName,
                deliveryRef,
                "reviewed-result-ancestor");
        }

        if (attributed.Count == 0)
            return ClassifyNotIntegrated(job, branchName, deliveryRef);

        var missing = new List<string>();
        foreach (var sha in attributed)
            if (!AncestorSetContains(reach.DevelopAncestors, sha)) missing.Add(sha);

        // NONE of the attributed commits landed → conflict-skipped / pending
        // (no-branch is impossible here: there IS attributed work).
        if (missing.Count == attributed.Count)
            return ClassifyNotIntegrated(job, branchName, deliveryRef);

        var newest = attributed[^1];

        // ALL attributed commits landed. Attempt history and recorded merge
        // provenance are deliberately irrelevant to this result.
        if (missing.Count == 0)
            return Integrated(Short(newest), branchName, deliveryRef, "anchor-ancestor");

        // SOME landed, some did not → partial, naming the missing short-SHAs so the
        // tooltip says exactly which attributed commits are not in develop yet.
        var integratedCount = attributed.Count - missing.Count;
        var missingShort = string.Join(", ", missing.Select(Short));
        return new TaskIntegrationStatus
        {
            Status = IntegrationStatuses.Partial,
            DeliveryRef = deliveryRef,
            IntegrationBranch = branchName,
            Detail = $"{integratedCount}/{attributed.Count} attributed commits integrated; "
                     + $"missing: {missingShort}",
        };
    }

    /// <summary>
    /// Splits a not-integrated card into conflict-skipped / pending / no-branch. A
    /// recorded merge-into-develop conflict / error wins (the work was NOT merged
    /// and needs a human); only a card with neither an attributed commit nor an
    /// evidenced delivery ref is no-branch; otherwise the work simply has not
    /// landed yet - pending.
    /// </summary>
    private TaskIntegrationStatus ClassifyNotIntegrated(
        TaskInfo job,
        string branchName,
        string? deliveryRef = null,
        List<TaskRepositoryIntegrationStatus>? repositories = null)
    {
        deliveryRef ??= DeliveryRefFor(job);
        var anchor = AnchorFor(job);
        var hasWork = anchor != null || deliveryRef != null;

        if (ReadIntegrationFailure(job) is { } failure)
        {
            var visibleReason = VisibleFailureReason(job, branchName, failure);
            return new TaskIntegrationStatus
            {
                Status = IntegrationStatuses.ConflictSkipped,
                DeliveryRef = deliveryRef,
                IntegrationBranch = branchName,
                Detail = visibleReason,
                Repositories = repositories ?? [],
                Failure = new TaskIntegrationFailure
                {
                    Code = failure.Code,
                    Label = failure.Label,
                    Reason = visibleReason,
                    RebaseRecoveryAvailable = failure.RebaseRecoveryAvailable,
                },
            };
        }

        if (!hasWork)
            return new TaskIntegrationStatus
            {
                Status = IntegrationStatuses.NoBranch,
                DeliveryRef = null,
                IntegrationBranch = branchName,
                Detail = "No delivery ref or attributed commit to integrate.",
                Repositories = repositories ?? [],
            };

        return new TaskIntegrationStatus
        {
            Status = IntegrationStatuses.Pending,
            DeliveryRef = deliveryRef,
            IntegrationBranch = branchName,
            Detail = deliveryRef is null
                ? $"Accepted work is not yet in {branchName}."
                : $"Delivery ref '{deliveryRef}' is not yet integrated into {branchName}.",
            Repositories = repositories ?? [],
        };
    }

    private static TaskIntegrationStatus Integrated(
        string sha,
        string branchName,
        string? deliveryRef,
        string detail) => new()
    {
        Status = IntegrationStatuses.Integrated,
        Sha = sha,
        DeliveryRef = deliveryRef,
        IntegrationBranch = branchName,
        Detail = detail,
    };

    /// <summary>
    /// Projects the same delivery-ref resolution used by acceptance onto the
    /// card. Immutable result refs, attributed commit branches, and canonical
    /// runner refs are durable card truth. The resolver's final
    /// <c>task/&lt;slug&gt;</c> compatibility value is only surfaced when
    /// provenance proves that a local task branch actually existed; otherwise
    /// it would recreate the ghost badge this projection is meant to remove.
    /// </summary>
    internal static string? DeliveryRefFor(TaskInfo job)
    {
        var resolved = DeliveryRefResolver.Resolve(job.Id, job.FolderPath);
        if (resolved.Source != DeliveryRefSource.LocalTaskFallback)
            return resolved.Ref;

        var attributedBranch = job.Commits
            .LastOrDefault(commit => !string.IsNullOrWhiteSpace(commit.Branch))
            ?.Branch
            ?? job.Commit?.Branch;
        if (!string.IsNullOrWhiteSpace(attributedBranch))
            return TaskIntegrationBranch.Name(attributedBranch, resolved.Ref);

        var hasLocalBranchEvidence = job.Provenance?.Transitions.Any(transition =>
            !string.IsNullOrWhiteSpace(transition.BranchTip)) == true;
        if (!hasLocalBranchEvidence)
            return null;

        return string.IsNullOrWhiteSpace(job.Provenance?.Branch)
            ? resolved.Ref
            : TaskIntegrationBranch.Name(job.Provenance!.Branch, resolved.Ref);
    }

    /// <summary>
    /// Reads and classifies the deferred merge-into-develop step outcome from
    /// the card's local <c>pipeline-execution.json</c>. Local file read only (no
    /// git spawn); best-effort. Legacy steps without a persisted failure code
    /// are classified from their stable verdict and reason vocabulary.
    /// </summary>
    private AcceptedIntegrationFailure? ReadIntegrationFailure(TaskInfo job)
    {
        if (!AcceptanceIntegrationPolicy.IsIntegrationRequired(job)) return null;

        var step = ReadLatestMergeStep(job);
        if (string.Equals(step?.Verdict, "operator-override", StringComparison.OrdinalIgnoreCase))
            return null;

        if (step is not null)
        {
            var mergeFailure = AcceptedIntegrationFailurePolicy.Classify(
                step.Status,
                step.Verdict,
                step.Reason,
                step.VerdictSummary,
                step.FailureCode);
            if (mergeFailure is not null) return mergeFailure;
        }

        // AGT-2688: the merge into the integration branch can succeed locally
        // while the deferred push to origin does not (main/develop lineage
        // block, or a genuinely diverged remote). That must not fall through to
        // plain "pending" - surface the push step's own terminal failure so
        // acceptance alarms instead of looping.
        var pushStep = ReadLatestPushStep(job);
        if (pushStep is null) return null;
        return AcceptedIntegrationFailurePolicy.Classify(
            pushStep.Status,
            pushStep.Verdict,
            pushStep.Reason,
            pushStep.VerdictSummary,
            pushStep.FailureCode);
    }

    private static string VisibleFailureReason(
        TaskInfo job,
        string branchName,
        AcceptedIntegrationFailure failure)
    {
        if (failure.Code != AcceptedIntegrationFailureCodes.MergeConflict)
            return failure.Reason;

        var delivery = ReviewSubjectStore.Read(job.FolderPath)?.ResultRef
            ?? WorktreeTaskLifecycle.BranchFor(job.Id);
        return $"{failure.Reason} Start the integration recovery action to run a steer round: "
               + $"rebase '{delivery}' onto the current integration branch '{branchName}', "
               + "resolve the conflicts, and deliver the updated branch.";
    }

    internal PipelineStepExecution? ReadLatestMergeStep(TaskInfo job)
    {
        try
        {
            return _pipelineLog.Read(job.FolderPath)?.Steps.LastOrDefault(step =>
                string.Equals(
                    step.StepId,
                    PipelineCatalogue.MergeIntoDevelopStepId,
                    StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "integration-status pipeline read failed for project={Project} job={JobId}",
                job.ProjectName,
                job.Id);
            return null;
        }
    }

    private PipelineStepExecution? ReadLatestPushStep(TaskInfo job)
    {
        try
        {
            return _pipelineLog.Read(job.FolderPath)?.Steps.LastOrDefault(step =>
                string.Equals(
                    step.StepId,
                    PipelineCatalogue.MergeIntoDevelopPushStepId,
                    StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "integration-status push-step read failed for project={Project} job={JobId}",
                job.ProjectName,
                job.Id);
            return null;
        }
    }

    private static bool IsDecidedIntegrationAttempt(PipelineStepExecution? step)
    {
        if (step is null) return false;
        if (string.Equals(step.Verdict, "conflict", StringComparison.OrdinalIgnoreCase)
            || string.Equals(step.Verdict, "pushed-for-review", StringComparison.OrdinalIgnoreCase)
            || string.Equals(step.Verdict, "gate-failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(step.Verdict, "error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(step.Verdict, "no-branch", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// The card anchor read entirely from the persisted board payload (no git
    /// spawn): the latest attributed TASK commit SHA. Null when the card committed
    /// nothing. Mirrors <see cref="BoardMergeStatusService.AnchorFor"/>.
    /// </summary>
    internal static string? AnchorFor(TaskInfo job)
    {
        var attributed = AttributedCommits(job);
        return attributed.Count == 0 ? null : attributed[^1];
    }

    /// <summary>
    /// The attributed commit SHAs the card's commit widget renders (oldest →
    /// newest), read entirely from the persisted board payload (no git spawn).
    /// Falls back to the legacy single <see cref="TaskInfo.Commit"/> when the list
    /// is empty, and drops blank SHAs plus zero-file platform lifecycle markers.
    /// A marker-shaped commit with changed files remains integrable work unless
    /// a later delivery attempt explicitly superseded it. Superseded entries
    /// remain in <c>commits[]</c> as history but are not current integration
    /// expectations. Empty when the card committed nothing.
    /// </summary>
    internal static IReadOnlyList<string> AttributedCommits(TaskInfo job)
        => AttributedCommits(job, null);

    internal static IReadOnlyList<TaskCommitInfo> AttributedCommitRecords(TaskInfo job)
    {
        var source = job.Commits.Count > 0
            ? job.Commits
            : job.Commit is null ? [] : [job.Commit];
        return source
            .Where(commit => !string.IsNullOrWhiteSpace(commit.Sha)
                && !TaskCommitSupersession.IsSuperseded(commit))
            .Select(TaskCommitRepository.NormalizeLegacy)
            .ToList();
    }

    /// <summary>
    /// Target-aware attribution filter. A reachable SHA is delivery truth even
    /// when stale persisted metadata makes it look like an empty lifecycle
    /// marker. The ancestry proof therefore outranks the marker heuristic.
    /// </summary>
    internal static IReadOnlyList<string> AttributedCommits(
        TaskInfo job,
        IReadOnlySet<string>? integrationAncestors)
    {
        var result = new List<string>(job.Commits.Count);
        if (job.Commits.Count > 0)
        {
            foreach (var c in job.Commits)
                if (!string.IsNullOrWhiteSpace(c.Sha)
                    && !TaskCommitSupersession.IsSuperseded(c)
                    && (!IsZeroFileLifecycleMarker(c)
                        || integrationAncestors is not null
                        && AncestorSetContains(integrationAncestors, c.Sha)))
                    result.Add(c.Sha);
        }
        else if (!string.IsNullOrWhiteSpace(job.Commit?.Sha)
                 && !TaskCommitSupersession.IsSuperseded(job.Commit!)
                 && (!IsZeroFileLifecycleMarker(job.Commit!)
                     || integrationAncestors is not null
                     && AncestorSetContains(integrationAncestors, job.Commit!.Sha)))
        {
            result.Add(job.Commit!.Sha);
        }
        return result;
    }

    /// <summary>
    /// Membership check for persisted task SHAs. Older task records can contain
    /// the seven-character display SHA rather than the full object id returned by
    /// <c>rev-list</c>. Treat a valid abbreviated SHA as landed when it prefixes a
    /// reachable full SHA; full SHAs still use exact membership.
    /// </summary>
    internal static bool AncestorSetContains(IReadOnlySet<string> ancestors, string sha)
    {
        if (string.IsNullOrWhiteSpace(sha)) return false;
        var candidate = sha.Trim();
        if (ancestors.Contains(candidate)) return true;
        if (candidate.Length is < 7 or >= 40 || !candidate.All(Uri.IsHexDigit))
            return false;

        return ancestors.Any(ancestor =>
            ancestor.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsZeroFileLifecycleMarker(TaskCommitInfo commit)
    {
        if (commit.FilesChanged != 0 || commit.Files.Count != 0)
            return false;

        var subject = commit.Message
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?.Trim() ?? "";
        return subject.StartsWith("wip(runner): salvage before teardown", StringComparison.OrdinalIgnoreCase)
               || subject.StartsWith("chore: snapshot for review", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The target-branch ancestor SHA set for one repository, cached per target
    /// HEAD fingerprint and computed under the read-only concurrency limiter.
    /// </summary>
    private RepoIntegration ComputeRepoIntegration(string root, string configuredBranch)
    {
        Interlocked.Increment(ref _computationCount);
        return ReadOnlyGitConcurrencyLimiter.Run(() =>
        {
            var integrationRef = _git.ResolveIntegrationReadRef(root, configuredBranch);
            var integrationBranch = integrationRef.StartsWith("origin/", StringComparison.Ordinal)
                ? integrationRef["origin/".Length..]
                : integrationRef;

            var succeeded = _git.TryGetAncestorShaSet(
                root,
                [integrationBranch, "origin/" + integrationBranch],
                out var ancestors);

            var releaseSucceeded = _git.TryGetAncestorShaSet(
                root,
                [BoardMergeStatusService.ReleaseBranch, "origin/" + BoardMergeStatusService.ReleaseBranch],
                out var releaseAncestors);

            return new RepoIntegration(
                integrationBranch,
                ancestors,
                releaseAncestors,
                succeeded && releaseSucceeded);
        });
    }

    private string ConfiguredIntegrationBranch(TaskInfo task)
    {
        return _settings.Get(task.ProjectName).IntegrationBranch;
    }

    internal void InvalidateCache() => _cache.Invalidate();
    internal int ComputationCount => Volatile.Read(ref _computationCount);

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private sealed record RepoIntegration(
        string IntegrationBranch,
        HashSet<string> DevelopAncestors,
        HashSet<string> ReleaseAncestors,
        bool Succeeded);

    private sealed record RepoBranchKey(string Root, string Branch);
    private sealed record RepositoryCommitGroup(
        string Repository,
        List<TaskCommitInfo> Commits,
        RepoBranchKey? Key,
        string IntegrationBranch);
}

internal enum AcceptedIntegrationRecoveryAction
{
    Ignore,
    Finalize,
    ReturnToReview,
    Retry,
}

internal sealed record AcceptedIntegrationRecoveryDecision(
    AcceptedIntegrationRecoveryAction Action,
    string Reason,
    PipelineStepExecution? LastMergeAttempt);
