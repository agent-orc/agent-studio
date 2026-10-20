namespace AgentStudio.Pipeline;

/// <summary>
/// Restart recovery for <see cref="IntegrationPushQueue"/>. The channel is an
/// in-process latency optimization, while the merge and push step outcomes in
/// <c>pipeline-execution.json</c> are durable. On startup and periodically this
/// service re-drives every successful acceptance merge whose integration push
/// has not reached a terminal success.
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
        GitService? git = null)
    {
        _scanner = scanner;
        _settings = settings;
        _pipeline = pipeline;
        _runner = runner;
        _configuration = configuration;
        _logger = logger;
        _git = git;
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var pushed = 0;
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
