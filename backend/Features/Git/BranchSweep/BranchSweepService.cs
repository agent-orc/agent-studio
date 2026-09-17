namespace AgentStudio.Git;

/// <summary>One ref the operator confirmed for deletion.</summary>
public sealed record BranchSweepExecutionItem(string Ref, string TipSha);

/// <summary>The confirmed selection posted to <see cref="BranchSweepService.Execute"/>.</summary>
public sealed record BranchSweepExecuteRequest(IReadOnlyList<BranchSweepExecutionItem> Items);

/// <summary>
/// Result of an operator-confirmed execute: how many refs were dropped, how
/// many were kept and why, and the per-ref detail.
/// </summary>
public sealed record BranchSweepExecutionResult(
    string Project,
    bool IsRepo,
    int DeletedCount,
    int KeptCount,
    IReadOnlyList<BranchSweepDeletion> Actions,
    string? Error);

/// <summary>
/// The stale-branch sweep (AGT-2794). Per project repository it fetches with
/// prune, classifies <b>every</b> remote ref through the shared
/// <see cref="BranchRetentionPolicy"/> (the one retention policy; this service
/// contributes facts, never a second rule set), writes a report, and - only in
/// <see cref="BranchSweepModes.Reclaim"/> - deletes what the policy allows.
///
/// <para>
/// Flow order per repository is deliberate: boundary checks, then fact
/// collection (refs, protected tips, worktrees, task index), then the pure
/// decision, then the bounded side effects (batched delete push, report write,
/// Activity line). <see cref="BranchSweepModes.ReportOnly"/> stops after the
/// decision, so no delete primitive is reachable in that mode.
/// </para>
/// </summary>
public sealed class BranchSweepService
{
    private const string OriginPrefix = "origin/";
    private const string RemoteRefRoot = "refs/remotes/origin";

    private readonly GitService _git;
    private readonly AgentStudio.Registry.ProjectRegistry _projects;
    private readonly ProjectSettingsService _settings;
    private readonly TaskScannerService _tasks;
    private readonly AttemptAuthorityService _attempts;
    private readonly BranchSweepReportStore _reports;
    private readonly ILogger<BranchSweepService> _logger;
    private readonly TimeProvider _time;
    private readonly AgentMessageBusBridge? _bus;

    public BranchSweepService(
        GitService git,
        AgentStudio.Registry.ProjectRegistry projects,
        ProjectSettingsService settings,
        TaskScannerService tasks,
        AttemptAuthorityService attempts,
        BranchSweepReportStore reports,
        ILogger<BranchSweepService> logger,
        TimeProvider? time = null,
        AgentMessageBusBridge? bus = null)
    {
        _git = git;
        _projects = projects;
        _settings = settings;
        _tasks = tasks;
        _attempts = attempts;
        _reports = reports;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _bus = bus;
    }

    /// <summary>Sweeps every registered project once, one report per project.</summary>
    public IReadOnlyList<BranchSweepReport> RunAll(CancellationToken cancellationToken = default)
    {
        var reports = new List<BranchSweepReport>();
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in _projects.List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = _git.ResolveProjectRepoRoot(project.Id);
            if (string.IsNullOrWhiteSpace(root)) continue;
            string normalized;
            try { normalized = Path.GetFullPath(root); }
            catch { normalized = root; }
            if (!seenRoots.Add(normalized)) continue;

            try
            {
                reports.Add(Run(project.DisplayName, modeOverride: null, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Branch sweep failed for project {Project}", project.DisplayName);
            }
        }
        return reports;
    }

    /// <summary>
    /// One sweep of one project. <paramref name="modeOverride"/> lets the
    /// operator force a report-only preview on a reclaim project (or the
    /// reverse) without changing the stored setting.
    /// </summary>
    public BranchSweepReport Run(
        string project,
        string? modeOverride = null,
        CancellationToken cancellationToken = default)
    {
        var startedAt = _time.GetUtcNow();
        var mode = modeOverride is null
            ? ResolveMode(project)
            : BranchSweepModes.Normalize(modeOverride);
        var windows = ResolveWindows(project);

        var classification = Classify(project, startedAt, windows, cancellationToken);
        if (classification.Error is not null)
        {
            var failed = BranchSweepReport.Failed(
                project, classification.RepositoryPath, mode, startedAt, classification.Error);
            _reports.Write(failed);
            return failed;
        }

        var candidates = classification.Candidates;
        var deletions = mode == BranchSweepModes.Reclaim
            ? DeleteRefs(
                classification.RepositoryPath!,
                candidates.Where(candidate => candidate.Eligible).ToList(),
                cancellationToken)
            : [];

        var deletedRefs = deletions.Where(d => d.Deleted).Select(d => d.Ref).ToHashSet(StringComparer.Ordinal);
        var report = new BranchSweepReport(
            project,
            classification.RepositoryPath,
            mode,
            startedAt,
            _time.GetUtcNow(),
            windows,
            candidates.Count,
            candidates.Count - deletedRefs.Count,
            BranchSweepReportBuilder.Totals(candidates, deletions),
            BranchSweepReportBuilder.AgeHistogram(candidates),
            candidates,
            deletions,
            null);

        _reports.Write(report);
        Announce(report);
        return report;
    }

    /// <summary>
    /// Read-only classification for the operator UI. Never writes a report and
    /// never deletes: it is the fresh plan the confirm-and-execute action is
    /// checked against.
    /// </summary>
    public BranchSweepReport Plan(string project, CancellationToken cancellationToken = default)
    {
        var startedAt = _time.GetUtcNow();
        var windows = ResolveWindows(project);
        var classification = Classify(project, startedAt, windows, cancellationToken);
        if (classification.Error is not null)
        {
            return BranchSweepReport.Failed(
                project, classification.RepositoryPath, ResolveMode(project), startedAt, classification.Error);
        }

        return new BranchSweepReport(
            project,
            classification.RepositoryPath,
            ResolveMode(project),
            startedAt,
            _time.GetUtcNow(),
            windows,
            classification.Candidates.Count,
            classification.Candidates.Count,
            BranchSweepReportBuilder.Totals(classification.Candidates, []),
            BranchSweepReportBuilder.AgeHistogram(classification.Candidates),
            classification.Candidates,
            [],
            null);
    }

    /// <summary>
    /// Deletes an operator-confirmed subset. Eligibility is re-derived from a
    /// fresh classification and the tip must still match the one the operator
    /// saw, so a stale or hand-crafted selection can never drop a ref the
    /// policy would keep. Mirrors <see cref="GitCleanupService.Execute"/>:
    /// explicit subset in, per-ref outcome out.
    /// </summary>
    public BranchSweepExecutionResult Execute(
        string project,
        BranchSweepExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var classification = Classify(project, now, ResolveWindows(project), cancellationToken);
        if (classification.Error is not null)
            return new BranchSweepExecutionResult(project, false, 0, 0, [], classification.Error);

        var eligible = classification.Candidates
            .Where(candidate => candidate.Eligible)
            .ToDictionary(candidate => candidate.Ref, StringComparer.Ordinal);

        var confirmed = new List<BranchSweepCandidate>();
        var rejected = new List<BranchSweepDeletion>();
        foreach (var item in request?.Items ?? [])
        {
            if (!eligible.TryGetValue(item.Ref, out var candidate))
            {
                rejected.Add(new BranchSweepDeletion(
                    item.Ref, BranchSweepClasses.Of(BranchRetentionPolicy.ClassifyNamespace(item.Ref)),
                    item.TipSha, false, "No longer eligible under the retention policy; kept."));
                continue;
            }
            if (!string.IsNullOrWhiteSpace(item.TipSha)
                && !string.Equals(item.TipSha, candidate.TipSha, StringComparison.OrdinalIgnoreCase))
            {
                rejected.Add(new BranchSweepDeletion(
                    item.Ref, candidate.Class, candidate.TipSha, false,
                    "Tip changed after the report was produced; kept."));
                continue;
            }
            confirmed.Add(candidate);
        }

        var actions = rejected
            .Concat(DeleteRefs(classification.RepositoryPath!, confirmed, cancellationToken))
            .ToList();
        var deleted = actions.Count(action => action.Deleted);
        _logger.LogInformation(
            "branch-sweep-executed project={Project} requested={Requested} deleted={Deleted} kept={Kept}",
            project, request?.Items.Count ?? 0, deleted, actions.Count - deleted);
        return new BranchSweepExecutionResult(
            project, true, deleted, actions.Count - deleted, actions, null);
    }

    public string ResolveMode(string project)
        => BranchSweepModes.Normalize(_settings.Get(project).BranchSweep?.Mode);

    public BranchRetentionWindows ResolveWindows(string project)
    {
        var sweep = _settings.Get(project).BranchSweep;
        var defaults = BranchRetentionWindows.Default;
        if (sweep is null) return defaults;
        return new BranchRetentionWindows(
            sweep.TaskRetentionDays ?? defaults.TaskDays,
            sweep.SalvageRetentionDays ?? defaults.SalvageDays,
            sweep.QuarantineRetentionDays ?? defaults.QuarantineDays,
            sweep.AbandonedRetentionDays ?? defaults.AbandonedDays).Clamped();
    }

    private sealed record Classification(
        string? RepositoryPath,
        IReadOnlyList<BranchSweepCandidate> Candidates,
        string? Error);

    /// <summary>
    /// Fact collection plus the pure decision for every remote ref. Containment
    /// against main and develop is answered with two <c>for-each-ref --merged</c>
    /// calls rather than one ancestry spawn per ref.
    /// </summary>
    private Classification Classify(
        string project,
        DateTimeOffset now,
        BranchRetentionWindows windows,
        CancellationToken cancellationToken)
    {
        var root = _git.ResolveProjectRepoRoot(project);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return new Classification(root, [], "Project has no resolvable git repository.");

        var fetch = _git.Fetch(root, cancellationToken: cancellationToken);
        if (!string.IsNullOrWhiteSpace(fetch.Error))
            return new Classification(root, [], $"Origin refresh failed; sweep skipped: {fetch.Error}");

        var main = ResolveProtectedRef(root, "main");
        var develop = ResolveProtectedRef(root, "develop");
        var inMain = main is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : _git.ListRefNamesMergedInto(root, RemoteRefRoot, main.Sha);
        var inDevelop = develop is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : _git.ListRefNamesMergedInto(root, RemoteRefRoot, develop.Sha);

        var checkedOut = _git.ListWorktrees(root)
            .Where(worktree => Directory.Exists(worktree.Path) && !string.IsNullOrWhiteSpace(worktree.Branch))
            .Select(worktree => worktree.Branch!)
            .ToHashSet(StringComparer.Ordinal);

        var taskStates = BuildTaskStateIndex(project, out var openKeys);
        var openRefs = BuildOpenCardRefIndex(openKeys);

        var candidates = new List<BranchSweepCandidate>();
        foreach (var reference in _git.ListRefs(root, RemoteRefRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var branch = reference.ShortName.StartsWith(OriginPrefix, StringComparison.Ordinal)
                ? reference.ShortName[OriginPrefix.Length..]
                : reference.ShortName;
            // origin/HEAD is a symbolic pointer at the default branch, not a ref
            // anyone can reclaim; it would otherwise be reported as unmanaged.
            if (branch.Length == 0 || branch == "HEAD") continue;

            var taskKey = BranchSweepPolicy.DeriveTaskKey(branch);
            candidates.Add(BranchSweepPolicy.Classify(
                new BranchSweepObservation(
                    branch,
                    reference.Sha,
                    reference.ShortSha,
                    reference.CommittedAtUtc,
                    inMain.Contains(reference.FullName),
                    inDevelop.Contains(reference.FullName),
                    main is not null,
                    develop is not null,
                    checkedOut.Contains(branch),
                    taskKey,
                    taskKey is null ? null : taskStates.GetValueOrDefault(taskKey),
                    openRefs.Contains(branch)),
                now,
                windows));
        }

        candidates.Sort((left, right) => string.CompareOrdinal(left.Ref, right.Ref));
        return new Classification(root, candidates, null);
    }

    /// <summary>
    /// Lane state for this project's cards, indexed by every identifier a ref
    /// name can carry. Managed refs are named after the card key
    /// (<c>info.Key</c>) with the internal task key and the job id as the
    /// historical fallbacks, exactly the ladder
    /// <c>TaskTransitionService.TriggerBranchReclaimAfterArchive</c> uses. A ref
    /// whose key is absent from this index is an orphan and the policy decides
    /// it by the age rule of its class.
    /// <paramref name="openKeys"/> carries the internal task keys of the
    /// non-archived cards, which is what the attempt index is addressed by.
    /// </summary>
    private Dictionary<string, string> BuildTaskStateIndex(string project, out IReadOnlyList<string> openKeys)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var open = new List<string>();
        foreach (var task in _tasks.ScanAllJobsWithArchive())
        {
            if (!string.Equals(task.ProjectName, project, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(task.Key)) index[task.Key!] = task.State;
            if (!string.IsNullOrWhiteSpace(task.TaskKey)) index.TryAdd(task.TaskKey, task.State);
            if (!string.IsNullOrWhiteSpace(task.Id)) index.TryAdd(task.Id, task.State);
            if (!string.Equals(task.State, TaskStates.Archive, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(task.TaskKey))
            {
                open.Add(task.TaskKey);
            }
        }
        openKeys = open;
        return index;
    }

    /// <summary>
    /// Branch names still referenced by an open (non-archived) card through its
    /// indexed delivery refs. These are recovery sources, so the sweep keeps
    /// them regardless of age.
    /// </summary>
    private HashSet<string> BuildOpenCardRefIndex(IReadOnlyList<string> openKeys)
    {
        var refs = new HashSet<string>(StringComparer.Ordinal);
        if (openKeys.Count == 0) return refs;
        foreach (var indexed in _attempts.GetIndexedDeliveryRefs(openKeys, includeArchived: false))
        {
            var branch = ToBranchName(indexed.Ref);
            if (branch is not null) refs.Add(branch);
        }
        return refs;
    }

    internal static string? ToBranchName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var branch = value.Trim();
        foreach (var prefix in new[] { "refs/remotes/origin/", "refs/heads/", "origin/" })
        {
            if (branch.StartsWith(prefix, StringComparison.Ordinal))
            {
                branch = branch[prefix.Length..];
                break;
            }
        }
        return branch.Length == 0 ? null : branch;
    }

    /// <summary>
    /// The only side effect that removes refs. Deletions go out in batches of at
    /// most <see cref="GitService.MaxRefsPerDeletePush"/> refs per push, with
    /// the expected tip leased per ref, so a branch that moved since
    /// classification is kept rather than truncated.
    /// </summary>
    private IReadOnlyList<BranchSweepDeletion> DeleteRefs(
        string repositoryPath,
        IReadOnlyList<BranchSweepCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0) return [];
        var byRef = candidates.ToDictionary(candidate => candidate.Ref, StringComparer.Ordinal);
        var outcomes = _git.DeleteRemoteBranchesAtTip(
            repositoryPath,
            candidates.Select(candidate => (candidate.Ref, candidate.TipSha)).ToList(),
            cancellationToken: cancellationToken);

        return outcomes
            .Select(outcome =>
            {
                var candidate = byRef.GetValueOrDefault(outcome.Branch);
                return new BranchSweepDeletion(
                    outcome.Branch,
                    candidate?.Class ?? BranchSweepClasses.Unmanaged,
                    candidate?.TipSha ?? string.Empty,
                    outcome.Deleted,
                    outcome.Deleted
                        ? candidate?.Reason ?? "Deleted."
                        : outcome.Error ?? "Deletion failed; ref kept.");
            })
            .ToList();
    }

    private GitRefLine? ResolveProtectedRef(string root, string branch)
    {
        var remote = _git.ListRefs(root, $"refs/remotes/origin/{branch}")
            .SingleOrDefault(reference => string.Equals(
                reference.FullName, $"refs/remotes/origin/{branch}", StringComparison.Ordinal));
        if (remote is not null) return remote;
        return _git.ListRefs(root, $"refs/heads/{branch}")
            .SingleOrDefault(reference => string.Equals(
                reference.FullName, $"refs/heads/{branch}", StringComparison.Ordinal));
    }

    /// <summary>One Activity-feed line per run with the run totals.</summary>
    private void Announce(BranchSweepReport report)
    {
        _logger.LogInformation(
            "branch-sweep-complete project={Project} mode={Mode} refs={Refs} eligible={Eligible} deleted={Deleted} failed={Failed}",
            report.Project, report.Mode, report.TotalRefs, report.EligibleCount,
            report.DeletedCount, report.DeleteFailedCount);
        if (_bus is null) return;
        try
        {
            _ = _bus.EmitBranchSweepAsync(report);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "branch-sweep-activity-line-skipped project={Project}", report.Project);
        }
    }
}
