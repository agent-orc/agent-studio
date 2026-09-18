namespace AgentStudio.Tasks;

/// <summary>One reconciled card and what the pass learned about it.</summary>
public sealed record IntegrationGenerationReconcileCard(
    string TaskKey,
    string StatusBefore,
    int SupersededCommits,
    int ContentIntegratedCommits,
    int MissingCommits,
    int MarkedCommits,
    string? Error);

public sealed record IntegrationGenerationReconcileReport(
    int Examined,
    int Cleared,
    int MarkedCommits,
    int Unresolved,
    IReadOnlyList<IntegrationGenerationReconcileCard> Cards)
{
    public static readonly IntegrationGenerationReconcileReport Empty = new(0, 0, 0, 0, []);
}

/// <summary>
/// AGT-2871 - re-evaluates Human Review cards that the integration projection
/// still reads as <c>pending</c> or <c>partial</c>, so a card whose current
/// delivery generation is long on the integration branch stops waiting for an
/// operator move.
///
/// <para>
/// The pass exists because one rule cannot be recomputed on the board's hot
/// path: a superseded generation whose commit was never rebased is not an
/// ancestor of the integration branch, and proving that it nevertheless adds
/// nothing costs a <c>git merge-tree --write-tree</c> per commit. This pass
/// pays that cost once for a bounded population, records the verdict on the
/// card, and the projection reads it from there forever after. Every other rule
/// (generation supersession, path-coverage supersession, breadth) is pure and
/// is re-derived on every read rather than trusted from disk.
/// </para>
///
/// <para>
/// It writes only additive state through <see cref="TaskMutationService"/>: no
/// attributed commit is rewritten or dropped, and a commit that is genuinely
/// missing is left undecided rather than stamped. Re-running the pass is a
/// no-op once the population is empty.
/// </para>
/// </summary>
public sealed class IntegrationGenerationReconcileSweep
{
    private readonly TaskScannerService _scanner;
    private readonly TaskIntegrationStatusService _integrationStatus;
    private readonly TaskMutationService _mutations;
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly ILogger<IntegrationGenerationReconcileSweep> _logger;

    public IntegrationGenerationReconcileSweep(
        TaskScannerService scanner,
        TaskIntegrationStatusService integrationStatus,
        TaskMutationService mutations,
        GitService git,
        ProjectSettingsService settings,
        ILogger<IntegrationGenerationReconcileSweep> logger)
    {
        _scanner = scanner;
        _integrationStatus = integrationStatus;
        _mutations = mutations;
        _git = git;
        _settings = settings;
        _logger = logger;
    }

    public IntegrationGenerationReconcileReport Run()
    {
        // Boundary: the population is exactly the stuck one - delivered cards
        // waiting in Human Review whose verdict is not yet a merged one.
        var humanReview = _scanner.ScanAllAutomationJobs()
            .Where(task => task.State == TaskStates.HumanReview)
            .Where(AcceptanceIntegrationPolicy.IsIntegrationRequired)
            .OrderBy(task => task.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(task => task.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (humanReview.Count == 0) return IntegrationGenerationReconcileReport.Empty;

        var statusByKey = _integrationStatus.BuildLookup(humanReview);
        var stuck = humanReview
            .Where(task => statusByKey.TryGetValue(task.TaskKey, out var status)
                           && status.Status is IntegrationStatuses.Pending or IntegrationStatuses.Partial)
            .ToList();
        if (stuck.Count == 0) return IntegrationGenerationReconcileReport.Empty;

        var rows = new List<IntegrationGenerationReconcileCard>(stuck.Count);
        var cleared = 0;
        var markedCommits = 0;
        var unresolved = 0;
        foreach (var task in stuck)
        {
            var before = statusByKey[task.TaskKey].Status;
            try
            {
                var row = Reconcile(task, before);
                rows.Add(row);
                if (row.Error is not null) unresolved++;
                else if (row.MissingCommits == 0) cleared++;
                markedCommits += row.MarkedCommits;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "integration-generation-reconcile-failed project={Project} task={Task}",
                    task.ProjectName,
                    task.Key ?? task.Id);
                rows.Add(new IntegrationGenerationReconcileCard(
                    task.TaskKey, before, 0, 0, 0, 0, ex.Message));
                unresolved++;
            }
        }

        if (markedCommits > 0) _integrationStatus.InvalidateCache();
        var report = new IntegrationGenerationReconcileReport(
            stuck.Count,
            cleared,
            markedCommits,
            unresolved,
            rows);
        _logger.LogInformation(
            "integration-generation-reconcile examined={Examined} cleared={Cleared} markedCommits={Marked} unresolved={Unresolved}",
            report.Examined,
            report.Cleared,
            report.MarkedCommits,
            report.Unresolved);
        return report;
    }

    private IntegrationGenerationReconcileCard Reconcile(TaskInfo task, string before)
    {
        // Application coordination: collect the repository facts the pure
        // policy needs, once per card.
        var root = _git.ResolveRepoRootForWatchPath(task.WatchPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return new IntegrationGenerationReconcileCard(
                task.TaskKey, before, 0, 0, 0, 0, "The project repository could not be resolved.");
        }

        var integrationRef = _git.ResolveIntegrationReadRef(
            root,
            _settings.Get(task.ProjectName).IntegrationBranch);
        if (!_git.TryGetAncestorShaSet(
                root,
                [integrationRef, _git.ResolveOriginReadRef(integrationRef)],
                out var ancestors))
        {
            return new IntegrationGenerationReconcileCard(
                task.TaskKey,
                before,
                0,
                0,
                0,
                0,
                $"The integration graph for '{integrationRef}' could not be read.");
        }

        var commits = TaskIntegrationStatusService.AttributedCommitRecords(task)
            .Select(commit => EnrichFiles(task, commit))
            .ToList();
        if (commits.Count == 0)
            return new IntegrationGenerationReconcileCard(task.TaskKey, before, 0, 0, 0, 0, null);

        // Pure decision, with the one git-backed fact the hot path cannot pay for.
        var verdict = DeliveryGenerationPolicy.Evaluate(
            commits,
            sha => TaskIntegrationStatusService.AncestorSetContains(ancestors, sha),
            commit => _git.MergeAddsNothing(root, integrationRef, commit.Sha));

        // Bounded side effect: additive evidence on task.json, nothing else.
        var write = _mutations.MarkCommitIntegrationEvidenceOnFolder(
            task.FolderPath,
            verdict.Commits.ToDictionary(
                commit => commit.Sha,
                commit => commit,
                StringComparer.OrdinalIgnoreCase));

        var contentIntegrated = verdict.Commits.Count(commit => string.Equals(
            commit.Rule, CommitIntegrationRules.ContentEqual, StringComparison.Ordinal));
        return new IntegrationGenerationReconcileCard(
            task.TaskKey,
            before,
            verdict.Superseded.Count,
            contentIntegrated,
            verdict.Missing.Count,
            write.MarkedCommits,
            write.Succeeded ? null : "The task mutation failed; no evidence was recorded.");
    }

    /// <summary>
    /// Path coverage is the legacy fallback's only input, so a record without
    /// changed-file metadata is filled in from git before the policy runs.
    /// </summary>
    private TaskCommitInfo EnrichFiles(TaskInfo task, TaskCommitInfo commit)
    {
        if (commit.Files.Count > 0) return commit;
        var files = _git.GetCommitFiles(task.Id, task.WatchPath, commit.Sha);
        return files.Count == 0 ? commit : commit with
        {
            FilesChanged = files.Count,
            Files = files.Select(file => file.Path).ToList(),
        };
    }
}
