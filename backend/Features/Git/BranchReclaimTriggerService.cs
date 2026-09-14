namespace AgentStudio.Git;

/// <summary>
/// Coordinates branch reclamation at task lifecycle transitions (integration, archive).
/// Failures are logged but do not block the transition. Evidence is recorded in
/// per-project reports in the workspace reports/ folder.
/// </summary>
public sealed class BranchReclaimTriggerService
{
    private readonly GitBranchRetentionService _retention;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BranchReclaimTriggerService> _logger;
    private readonly TimelineLog? _timeline;

    public BranchReclaimTriggerService(
        GitBranchRetentionService retention,
        IConfiguration configuration,
        ILogger<BranchReclaimTriggerService> logger,
        TimelineLog? timeline = null)
    {
        _retention = retention;
        _configuration = configuration;
        _logger = logger;
        _timeline = timeline;
    }

    /// <summary>
    /// Best-effort per-task echo of a reclaim report into that task's own
    /// timeline, alongside the project-wide <see cref="BranchRetentionEvidenceWriter"/>
    /// row every deleted ref already got inside <see cref="GitBranchRetentionService"/>.
    /// No-op without a resolved task folder (e.g. the project-wide promotion
    /// sweep, which is not tied to one task) or without any deletions.
    /// </summary>
    private void AppendTimelineEcho(string? taskFolderPath, BranchRetentionProjectReport report)
    {
        if (_timeline is null || string.IsNullOrWhiteSpace(taskFolderPath) || report.DeletedCount == 0) return;
        var deleted = report.Actions.Where(a => a.Deleted).ToList();
        _timeline.Append(
            taskFolderPath,
            TimelineEventKinds.BranchesReclaimed,
            TimelineActors.System,
            $"Reclaimed {deleted.Count} git ref(s): {string.Join(", ", deleted.Select(a => a.Branch))}.",
            details: new Dictionary<string, string>
            {
                ["refs"] = string.Join(",", deleted.Select(a => a.Branch)),
                ["shas"] = string.Join(",", deleted.Select(a => a.TipSha)),
            });
    }

    /// <summary>
    /// Same settings gate as <see cref="GitBranchRetentionHostedService"/>
    /// (<c>GitRetention:Enabled</c>, default on) so an operator who disables
    /// the periodic sweep also disables the event-driven triggers.
    /// </summary>
    private bool IsEnabled() => _configuration.GetValue<bool?>("GitRetention:Enabled") ?? true;

    /// <summary>
    /// Trigger reclamation after successful integration into the configured
    /// integration branch (typically develop). This runs asynchronously and
    /// does not block the integration transition.
    /// </summary>
    public void ReclaimAfterIntegration(
        string project,
        string repositoryPath,
        string taskKey,
        string integrationBranch,
        string? taskFolderPath = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled()) return;
        try
        {
            var report = _retention.ReclaimForTask(
                project,
                repositoryPath,
                taskKey,
                integrationBranch,
                isArchive: false,
                cancellationToken);

            if (report.Error is not null)
            {
                _logger.LogWarning(
                    "Branch reclaim after integration had errors for {Project}/{TaskKey}: {Error}",
                    project, taskKey, report.Error);
            }
            else if (report.DeletedCount > 0)
            {
                _logger.LogInformation(
                    "Branch reclaim after integration for {Project}/{TaskKey}: deleted={Deleted} kept={Kept}",
                    project, taskKey, report.DeletedCount, report.KeptCount);
                AppendTimelineEcho(taskFolderPath, report);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Branch reclaim after integration failed for {Project}/{TaskKey}",
                project, taskKey);
        }
    }

    /// <summary>
    /// Trigger reclamation when a task is archived. This evaluates salvage refs
    /// and quarantine refs associated with the task, as well as task/runner/delivery
    /// refs that are no longer needed.
    /// </summary>
    public void ReclaimAfterArchive(
        string project,
        string repositoryPath,
        string taskKey,
        string? taskFolderPath = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled()) return;
        try
        {
            var report = _retention.ReclaimForTask(
                project,
                repositoryPath,
                taskKey,
                integrationBranch: "develop",
                isArchive: true,
                cancellationToken);

            if (report.Error is not null)
            {
                _logger.LogWarning(
                    "Branch reclaim after archive had errors for {Project}/{TaskKey}: {Error}",
                    project, taskKey, report.Error);
            }
            else if (report.DeletedCount > 0)
            {
                _logger.LogInformation(
                    "Branch reclaim after archive for {Project}/{TaskKey}: deleted={Deleted} kept={Kept}",
                    project, taskKey, report.DeletedCount, report.KeptCount);
                AppendTimelineEcho(taskFolderPath, report);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Branch reclaim after archive failed for {Project}/{TaskKey}",
                project, taskKey);
        }
    }

    /// <summary>
    /// Trigger reclamation for all eligible refs in a project after promotion
    /// to main. This evaluates all six namespaces (task, runner, delivery, results,
    /// salvage, quarantine) for deletion eligibility.
    /// </summary>
    public void ReclaimAfterPromotionToMain(
        string project,
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled()) return;
        try
        {
            var report = _retention.RunOnce(dryRun: false, cancellationToken);

            if (report.Projects.Count == 0)
            {
                _logger.LogInformation("Branch reclaim after promotion to main: no projects");
                return;
            }

            var projectReport = report.Projects.FirstOrDefault(p =>
                string.Equals(p.RepositoryPath, repositoryPath, StringComparison.OrdinalIgnoreCase));

            if (projectReport is not null)
            {
                if (projectReport.Error is not null)
                {
                    _logger.LogWarning(
                        "Branch reclaim after promotion to main had errors for {Project}: {Error}",
                        project, projectReport.Error);
                }
                else if (projectReport.DeletedCount > 0)
                {
                    _logger.LogInformation(
                        "Branch reclaim after promotion to main for {Project}: deleted={Deleted} kept={Kept}",
                        project, projectReport.DeletedCount, projectReport.KeptCount);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Branch reclaim after promotion to main failed for {Project}",
                project);
        }
    }
}
