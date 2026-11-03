namespace AgentStudio.Pipeline;

internal enum PendingPushRecoveryDisposition { Ignore, Clear, Push }

/// <summary>
/// Restart recovery for <see cref="IntegrationPushQueue"/>. The channel is an
/// in-process latency optimization, while the merge and push step outcomes in
/// <c>pipeline-execution.json</c> are durable. On startup and periodically this
/// service first re-drives persisted pending-push reservations, including cards
/// still in Auto Review, then recovers older accepted cards from pipeline facts.
/// </summary>
public sealed class IntegrationPushBackstopHostedService : BackgroundService
{
    private readonly TaskScannerService _scanner;
    private readonly ProjectSettingsService _settings;
    private readonly PipelineExecutionLog _pipeline;
    private readonly MergeIntoDevelopRunner _runner;
    private readonly IConfiguration _configuration;
    private readonly ILogger<IntegrationPushBackstopHostedService> _logger;
    private readonly GitService? _git;
    private readonly BatchIntegrationReservationService? _reservations;

    /// <param name="git">
    /// Proves a superseded push (see <see cref="RecordSupersededPush"/>). Legacy
    /// fixtures construct the backstop without it and keep retrying such a push.
    /// </param>
    public IntegrationPushBackstopHostedService(
        TaskScannerService scanner,
        ProjectSettingsService settings,
        PipelineExecutionLog pipeline,
        MergeIntoDevelopRunner runner,
        IConfiguration configuration,
        ILogger<IntegrationPushBackstopHostedService> logger,
        GitService? git = null,
        BatchIntegrationReservationService? reservations = null)
    {
        _scanner = scanner;
        _settings = settings;
        _pipeline = pipeline;
        _runner = runner;
        _configuration = configuration;
        _logger = logger;
        _git = git;
        _reservations = reservations;
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var pushed = 0;
        // The persisted reservation is the push job for per-task integrations.
        // It can belong to a card still in Auto Review, so the completed-card
        // scan below cannot recover it after a process restart.
        if (_reservations is not null)
        {
            _reservations.LoadPersistedProjects(_settings.GetAll().Keys);
            foreach (var project in _reservations.ProjectsWithPending())
            foreach (var marker in _reservations.PendingForProject(project))
            {
                ct.ThrowIfCancellationRequested();
                var folder = _scanner.FindJob(marker.JobId, marker.WatchPath)?.FolderPath
                    ?? marker.JobFolderPath;
                if (folder is null) continue;
                var record = _pipeline.Read(folder);
                var merge = record?.Steps.LastOrDefault(step =>
                    step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
                var priorPush = record?.Steps.LastOrDefault(step =>
                    step.StepId == PipelineCatalogue.MergeIntoDevelopPushStepId);
                var disposition = DecideMarker(marker, merge, priorPush);
                if (disposition == PendingPushRecoveryDisposition.Ignore) continue;
                if (disposition == PendingPushRecoveryDisposition.Clear)
                {
                    _reservations.MarkPushVerified(project, marker.JobId, marker.ApprovedSha);
                    continue;
                }
                var branch = marker.IntegrationBranch
                    ?? _settings.Get(project).IntegrationBranch;
                var result = await _runner.PushIntegrationBranchAsync(project,
                    marker.JobId, folder, marker.WatchPath ?? marker.RepositoryPath,
                    branch, ct, marker.ApprovedSha).ConfigureAwait(false);
                if (result.Success)
                {
                    _reservations.MarkPushVerified(project, marker.JobId, marker.ApprovedSha);
                    pushed++;
                }
                else
                {
                    // The runner records the failed push step on the card.
                    // A failed job must release the batch gate for repair.
                    _reservations.MarkPushVerified(project, marker.JobId, marker.ApprovedSha);
                    _logger.LogWarning("integration-push-backstop failed project={Project} job={JobId} status={Status}: {Reason}",
                        project, marker.JobId, result.Status, result.Error);
                }
            }
        }
        var accepted = _scanner.ScanAllAutomationJobsWithArchive()
            .Where(job => job.State is TaskStates.Completed or TaskStates.Archive)
            .Where(job => File.Exists(Path.Combine(job.FolderPath, PipelineExecutionLog.FileName)))
            .OrderBy(job => job.EnteredLaneAt)
            .ThenBy(job => job.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var job in accepted)
        {
            ct.ThrowIfCancellationRequested();
            var record = _pipeline.Read(job.FolderPath);
            var merge = record?.Steps.LastOrDefault(
                step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
            if (merge?.Status != PipelineStepStatus.Passed) continue;

            var previousPush = record?.Steps.LastOrDefault(
                step => step.StepId == PipelineCatalogue.MergeIntoDevelopPushStepId);
            if (previousPush?.Status is PipelineStepStatus.Passed or PipelineStepStatus.Skipped) continue;

            // A passed merge step authorizes only its recorded commit. A gate
            // may begin after this scan, or an interrupted gate may outlive the
            // process that held the in-memory busy flag. Never infer approval
            // from the integration lane's current tip.
            if (!ReviewSubjectStore.IsValidResultSha(merge.ApprovedIntegrationSha))
            {
                _logger.LogWarning(
                    "integration-push-backstop blocked project={Project} job={JobId}: passed merge has no durable approved SHA; revalidate integration before publishing",
                    job.ProjectName, job.Id);
                continue;
            }

            var settings = PipelineTypeSettings.ForTask(_settings.Get(job.ProjectName), job)!;
            if (!PipelineStepConfigResolver.IsEnabled(settings, PipelineCatalogue.MergeIntoDevelopPushStepId))
                continue;

            var result = await _runner.PushIntegrationBranchAsync(
                job.ProjectName,
                job.Id,
                job.FolderPath,
                job.WatchPath,
                settings.IntegrationBranch,
                ct,
                merge.ApprovedIntegrationSha);
            if (result.Success) pushed++;
            else if (result.Status == "sha-not-on-branch")
                RecordSupersededPush(job, settings.IntegrationBranch, merge.ApprovedIntegrationSha!);
        }

        if (pushed > 0)
        {
            _logger.LogInformation(
                "integration-push-backstop recovered {Count} push(es) after a missed queue window",
                pushed);
        }
        return pushed;
    }

    internal static PendingPushRecoveryDisposition DecideMarker(PendingIntegrationPush marker,
        PipelineStepExecution? merge, PipelineStepExecution? push)
    {
        if (merge?.Status != PipelineStepStatus.Passed
            || !ReviewSubjectStore.IsValidResultSha(marker.ApprovedSha)
            || !string.Equals(merge.ApprovedIntegrationSha, marker.ApprovedSha,
                StringComparison.OrdinalIgnoreCase))
            return PendingPushRecoveryDisposition.Ignore;
        // A prior success is not proof that origin still contains the approved
        // SHA. Re-enter the push path so git verifies the current remote tip.
        return push is { StartedAt: { } started }
            && merge.CompletedAt is { } mergedAt
            && started >= mergedAt
            && push.Status == PipelineStepStatus.Failed
            ? PendingPushRecoveryDisposition.Clear
            : PendingPushRecoveryDisposition.Push;
    }

    /// <summary>
    /// An approved merge result that never reached the integration line cannot
    /// be pushed, and the refusal repeats on every sweep. On a completed or
    /// archived card whose attributed delivery commits all are on
    /// <c>origin/&lt;branch&gt;</c> already (an operator merge, for example),
    /// nothing is left to publish: the push step becomes a terminal Skipped.
    /// Without that proof the step stays failed and the next sweep retries it.
    /// </summary>
    private void RecordSupersededPush(TaskInfo job, string integrationBranch, string approvedSha)
    {
        if (_git is null || job.State is not (TaskStates.Completed or TaskStates.Archive)) return;
        var repoRoot = _git.ResolveRepoRootForWatchPath(job.WatchPath)
            ?? (string.IsNullOrWhiteSpace(job.WatchPath) ? null : job.WatchPath);
        if (string.IsNullOrWhiteSpace(repoRoot)) return;
        var branch = _git.ResolveIntegrationBranch(repoRoot, integrationBranch);
        var delivery = TaskIntegrationStatusService.AttributedCommits(job);
        if (delivery.Count == 0
            || !delivery.All(sha => _git.IsAncestor(repoRoot, sha, "origin/" + branch)))
            return;
        if (_pipeline.Read(job.FolderPath) is not { } record) return;
        using var attempt = _pipeline.EnterAttempt(job.FolderPath, record.Attempt);
        var at = DateTime.UtcNow;
        _pipeline.RecordStep(job.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopPushStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Skipped,
            StartedAt = at,
            CompletedAt = at,
            DurationMs = 0,
            Verdict = "superseded",
            Reason = $"Terminal superseded: the approved merge result {approvedSha} is not on the "
                + $"integration line, and all {delivery.Count} attributed delivery commit(s) are already "
                + $"on origin/{branch}. Nothing is left to push.",
        });
        _logger.LogInformation(
            "integration-push-backstop superseded project={Project} job={JobId} approved={ApprovedSha} branch={Branch}",
            job.ProjectName, job.Id, approvedSha, branch);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _scanner.WaitForFirstPublicationAsync(stoppingToken).ConfigureAwait(false);
        var interval = TimeSpan.FromMinutes(Math.Clamp(
            _configuration.GetValue<int?>("Integration:PushBackstopIntervalMinutes") ?? 15,
            1,
            24 * 60));
        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Integration push backstop sweep failed");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
