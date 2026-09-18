namespace AgentStudio.Tasks;

/// <summary>One reconciled card: what it read before, and which rule answered for which commit.</summary>
public sealed record IntegrationGenerationReconcileRow(
    string Project,
    string TaskKey,
    string Lane,
    string? IntegrationRef,
    string PreviousStatus,
    IReadOnlyList<string> Decisions,
    int WrittenCommits,
    string? Error);

/// <summary>Result of one reconciliation pass over the Human Review lane.</summary>
public sealed record IntegrationGenerationReconcileReport(
    DateTime RanAtUtc,
    int Candidates,
    int RepairedTasks,
    int RepairedCommits,
    int ContentProbes,
    IReadOnlyList<IntegrationGenerationReconcileRow> Rows)
{
    public static IntegrationGenerationReconcileReport Empty(DateTime ranAtUtc)
        => new(ranAtUtc, 0, 0, 0, 0, []);
}

/// <summary>
/// AGT-2871 - re-evaluates the Human Review cards that read <c>pending</c> or
/// <c>partial</c> and resolves the delivery generations their commit history
/// mixes. Nine reviewed, merged cards sat in <c>5-human-review</c> for days
/// because a first-round <c>wip(runner): salvage before teardown</c> commit
/// never reached <c>develop</c>: the later generation had re-authored the same
/// files, so the old commit could not be an ancestor and the card stayed
/// incomplete forever, which also stopped the acceptance rail from completing
/// it.
///
/// <para>
/// The pass owns the two decisions the board projection deliberately cannot
/// make on its hot path: the content answer
/// (<c>git merge-tree --write-tree</c>, one git process per commit) and the
/// durable record of which rule decided. It writes only the two evidence
/// fields of the commits it resolved - every attributed commit stays in
/// <c>task.json</c> exactly where it was - and never touches a lane, a branch,
/// or a worktree. The next projection read sees the persisted verdict, flips
/// the card to <c>integrated</c>, and the acceptance rail completes it without
/// an operator move.
/// </para>
///
/// <para>
/// A commit of the <em>current</em> generation that is genuinely missing is
/// left exactly as it is, so it keeps blocking acceptance.
/// </para>
/// </summary>
public sealed class IntegrationGenerationReconciler
{
    /// <summary>
    /// Content probes one pass may spend. The probe is a git process per
    /// commit, so a lane full of genuinely incomplete cards cannot turn the
    /// periodic reconcile into a git storm.
    /// </summary>
    internal const int MaxContentProbesPerPass = 60;

    private static readonly HashSet<string> ReconciledStatuses = new(StringComparer.Ordinal)
    {
        IntegrationStatuses.Pending,
        IntegrationStatuses.Partial,
    };

    private readonly TaskScannerService _scanner;
    private readonly TaskIntegrationStatusService _integrationStatus;
    private readonly TaskMutationService _mutations;
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<IntegrationGenerationReconciler> _logger;

    /// <summary>
    /// Per card, the integration tip it was last evaluated against. Unchanged
    /// facts are skipped, so the periodic pass costs nothing while the lane and
    /// the integration branch stand still. In memory on purpose: a restart is a
    /// new fact and costs exactly one re-evaluation per card.
    /// </summary>
    private readonly Dictionary<string, string> _evaluated = new(StringComparer.Ordinal);

    public IntegrationGenerationReconciler(
        TaskScannerService scanner,
        TaskIntegrationStatusService integrationStatus,
        TaskMutationService mutations,
        GitService git,
        ProjectSettingsService settings,
        ILogger<IntegrationGenerationReconciler> logger,
        TimeProvider? timeProvider = null)
    {
        _scanner = scanner;
        _integrationStatus = integrationStatus;
        _mutations = mutations;
        _git = git;
        _settings = settings;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public IntegrationGenerationReconcileReport RunOnce(CancellationToken ct = default)
    {
        var ranAt = _time.GetUtcNow().UtcDateTime;
        var jobs = _scanner.ScanAllAutomationJobs()
            .Where(job => job.State == TaskStates.HumanReview)
            .OrderBy(job => job.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(job => job.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (jobs.Count == 0) return IntegrationGenerationReconcileReport.Empty(ranAt);

        var statusByKey = _integrationStatus.BuildLookup(jobs);
        // Only a card with more than one attributed commit can mix delivery
        // generations; a single-commit card that is not in the branch is simply
        // not integrated yet.
        var candidates = jobs
            .Where(job => statusByKey.TryGetValue(job.TaskKey, out var status)
                          && ReconciledStatuses.Contains(status.Status))
            .Where(job => TaskIntegrationStatusService.AllAttributedCommitRecords(job).Count > 1)
            .ToList();
        if (candidates.Count == 0) return IntegrationGenerationReconcileReport.Empty(ranAt);

        var rows = new List<IntegrationGenerationReconcileRow>();
        var repairedTasks = 0;
        var repairedCommits = 0;
        var probes = 0;
        foreach (var job in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var previousStatus = statusByKey[job.TaskKey].Status;
            try
            {
                var root = _git.ResolveRepoRootForWatchPath(job.WatchPath);
                if (string.IsNullOrWhiteSpace(root))
                {
                    rows.Add(Unresolved(job, previousStatus, "The project repository could not be resolved."));
                    continue;
                }

                var integrationRef = _git.ResolveIntegrationReadRef(
                    root,
                    _settings.Get(job.ProjectName).IntegrationBranch);
                if (!_git.TryGetAncestorShaSet(
                        root,
                        [integrationRef, _git.ResolveOriginReadRef(integrationRef)],
                        out var ancestors))
                {
                    rows.Add(Unresolved(
                        job,
                        previousStatus,
                        $"The integration graph for '{integrationRef}' could not be read."));
                    continue;
                }

                var records = TaskIntegrationStatusService.AllAttributedCommitRecords(job)
                    .Select(commit => EnrichFiles(job, commit))
                    .ToList();
                if (!ShouldEvaluate(job, root, integrationRef, records.Count)) continue;

                var verdict = DeliveryGenerationPolicy.Evaluate(
                    records,
                    sha => TaskIntegrationStatusService.AncestorSetContains(ancestors, sha),
                    commit =>
                    {
                        if (probes >= MaxContentProbesPerPass) return false;
                        probes++;
                        return _git.IsContentContainedInBranch(root, commit.Sha, integrationRef);
                    });

                var outcomes = verdict.Commits
                    .Where(commit => CommitIntegrationEvidence.IsDurable(commit.Evidence))
                    .ToDictionary(
                        commit => commit.Sha,
                        commit => new CommitIntegrationOutcome(
                            commit.Evidence,
                            ReplacementAttempt(records, commit)),
                        StringComparer.OrdinalIgnoreCase);
                var decisions = verdict.Commits
                    .Where(commit => !string.Equals(
                        commit.Evidence,
                        CommitIntegrationEvidence.Ancestor,
                        StringComparison.Ordinal))
                    .Select(commit => commit.ReplacementSha is null
                        ? $"{Short(commit.Sha)} {commit.Evidence}"
                        : $"{Short(commit.Sha)} {commit.Evidence} by {Short(commit.ReplacementSha)}")
                    .ToList();

                var write = outcomes.Count == 0
                    ? new CommitSupersessionWriteResult(true, 0)
                    : _mutations.MarkCommitIntegrationEvidenceOnFolder(job.FolderPath, outcomes);
                if (!write.Succeeded)
                {
                    rows.Add(Unresolved(
                        job,
                        previousStatus,
                        "The task mutation failed; no commit evidence was written.",
                        integrationRef,
                        decisions));
                    continue;
                }

                if (write.MarkedCommits > 0)
                {
                    repairedTasks++;
                    repairedCommits += write.MarkedCommits;
                    _integrationStatus.InvalidateCache();
                }
                if (decisions.Count > 0 || write.MarkedCommits > 0)
                {
                    rows.Add(new IntegrationGenerationReconcileRow(
                        job.ProjectName,
                        job.Key ?? job.Id,
                        job.State,
                        integrationRef,
                        previousStatus,
                        decisions,
                        write.MarkedCommits,
                        null));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "integration-generation-reconcile failed project={Project} task={Task}",
                    job.ProjectName,
                    job.Key ?? job.Id);
                rows.Add(Unresolved(job, previousStatus, ex.Message));
            }
        }

        if (repairedTasks > 0 || rows.Any(row => row.Error is not null))
        {
            _logger.LogInformation(
                "integration-generation-reconcile candidates={Candidates} repairedTasks={Tasks} "
                + "repairedCommits={Commits} contentProbes={Probes}",
                candidates.Count,
                repairedTasks,
                repairedCommits,
                probes);
        }

        return new IntegrationGenerationReconcileReport(
            ranAt,
            candidates.Count,
            repairedTasks,
            repairedCommits,
            probes,
            rows);
    }

    /// <summary>
    /// Whether this card's facts changed since the last pass. The integration
    /// tip plus the commit count is the whole input: a card whose current
    /// generation is genuinely missing is re-evaluated exactly when the branch
    /// moves or the card delivers again.
    /// </summary>
    private bool ShouldEvaluate(TaskInfo job, string root, string integrationRef, int commitCount)
    {
        var tip = _git.GetBranchTip(root, integrationRef) ?? integrationRef;
        var fingerprint = $"{tip}|{commitCount}";
        var key = $"{job.ProjectName}\0{job.TaskKey}";
        lock (_evaluated)
        {
            if (_evaluated.TryGetValue(key, out var previous)
                && string.Equals(previous, fingerprint, StringComparison.Ordinal))
            {
                return false;
            }
            _evaluated[key] = fingerprint;
        }
        return true;
    }

    /// <summary>
    /// The replacement generation to record beside a supersession verdict: the
    /// replacing commit's run attempt, its verified result, or the commit id
    /// itself. Null for a content-equal verdict - nothing replaced that commit,
    /// its work is simply already in the branch.
    /// </summary>
    private static string? ReplacementAttempt(
        IReadOnlyList<TaskCommitInfo> records,
        DeliveryGenerationCommit commit)
    {
        if (!commit.IsSuperseded || commit.ReplacementSha is null) return null;
        var replacement = records.FirstOrDefault(record => string.Equals(
            record.Sha,
            commit.ReplacementSha,
            StringComparison.OrdinalIgnoreCase));
        if (replacement is null) return commit.ReplacementSha;
        return !string.IsNullOrWhiteSpace(replacement.RunAttemptId)
            ? replacement.RunAttemptId
            : !string.IsNullOrWhiteSpace(replacement.ResultSha)
                ? replacement.ResultSha
                : replacement.Sha;
    }

    private TaskCommitInfo EnrichFiles(TaskInfo job, TaskCommitInfo commit)
    {
        if (commit.Files.Count > 0) return commit;
        var files = _git.GetCommitFiles(job.Id, job.WatchPath, commit.Sha);
        return files.Count == 0 ? commit : commit with
        {
            FilesChanged = files.Count,
            Files = files.Select(file => file.Path).ToList(),
        };
    }

    private static IntegrationGenerationReconcileRow Unresolved(
        TaskInfo job,
        string previousStatus,
        string error,
        string? integrationRef = null,
        IReadOnlyList<string>? decisions = null)
        => new(
            job.ProjectName,
            job.Key ?? job.Id,
            job.State,
            integrationRef,
            previousStatus,
            decisions ?? [],
            0,
            error);

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
