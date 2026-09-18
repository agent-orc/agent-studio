using AgentStudio.Git;
using AgentStudio.Tasks;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// Fires the bounded-backoff infrastructure retries recorded by
/// <see cref="AttemptAuthorityService.ScheduleReviewInfrastructureRetry"/>.
/// <c>/reviews/attempts/{id}/report</c> only decides WHEN a retry is due and
/// writes that decision onto the card; this service is the driver that mints
/// the successor ReviewAttempt once the delay elapses, so a card recovers from
/// a transient host or provider fault (AspectTimeout, ToolUnavailable,
/// BaselineUnavailable, a workspace failure, ...) without an operator having to
/// move the card to get a new attempt (AGT-2841).
/// </summary>
public sealed class ReviewInfrastructureRetryScheduler : BackgroundService
{
    public const int DefaultIntervalSeconds = 15;
    public const int DefaultMissingAttemptTimeoutMinutes = 30;

    private readonly AttemptAuthorityService _authority;
    private readonly ReviewAttemptTaskLifecycleService _lifecycle;
    private readonly TaskScannerService _scanner;
    private readonly AgentStudio.Registry.ProjectRegistry _projects;
    private readonly AgentStudio.Projects.ProjectSettingsService _settings;
    private readonly RemoteReviewPlanBuilder _remoteReviewPlans;
    private readonly GitService _git;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReviewInfrastructureRetryScheduler> _logger;

    public ReviewInfrastructureRetryScheduler(
        AttemptAuthorityService authority,
        ReviewAttemptTaskLifecycleService lifecycle,
        TaskScannerService scanner,
        AgentStudio.Registry.ProjectRegistry projects,
        AgentStudio.Projects.ProjectSettingsService settings,
        RemoteReviewPlanBuilder remoteReviewPlans,
        GitService git,
        IConfiguration configuration,
        ILogger<ReviewInfrastructureRetryScheduler> logger)
    {
        _authority = authority;
        _lifecycle = lifecycle;
        _scanner = scanner;
        _projects = projects;
        _settings = settings;
        _remoteReviewPlans = remoteReviewPlans;
        _git = git;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Fires every currently-due retry once. Returns the number of successor
    /// ReviewAttempts created. Public and synchronous so tests can drive a
    /// single deterministic tick instead of racing the background loop.
    /// </summary>
    public int RunOnce()
    {
        var due = _authority.DueReviewInfrastructureRetries();
        var created = 0;
        foreach (var item in due)
        {
            try
            {
                if (FireDue(item)) created++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "review-infrastructure-retry-fire-failed attempt={AttemptId} task={TaskKey}",
                    item.AttemptId,
                    item.TaskKey);
            }
        }
        created += RecoverMissingReviewAttempts();
        return created;
    }

    /// <summary>
    /// Repairs an Auto Review card whose handoff never produced a canonical
    /// ReviewAttempt. This is deliberately separate from AGT-2841: a terminal
    /// ReviewInfra attempt with a scheduled successor is not "missing" and is
    /// left to the bounded-backoff path above.
    /// </summary>
    private int RecoverMissingReviewAttempts()
    {
        var timeoutMinutes = Math.Clamp(
            _configuration.GetValue<int?>("Runner:ReviewMissingAttemptTimeoutMinutes")
            ?? DefaultMissingAttemptTimeoutMinutes,
            1,
            24 * 60);
        var cutoff = DateTime.UtcNow.AddMinutes(-timeoutMinutes);
        var created = 0;
        foreach (var task in _scanner.ScanAllAutomationJobs()
                     .Where(task => string.Equals(task.State, TaskStates.AutoReview, StringComparison.OrdinalIgnoreCase))
                     .Where(task => task.ParkedBlocker is null)
                     .Where(task => task.EnteredLaneAt != default && task.EnteredLaneAt.ToUniversalTime() <= cutoff))
        {
            var candidateKeys = new[] { task.Key, task.Id, task.TaskKey }
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var taskKey = candidateKeys[0];
            var projection = _authority.GetTaskProjection(taskKey);
            foreach (var candidateKey in candidateKeys.Skip(1))
            {
                if (projection.CurrentRunAttempt is not null || projection.CurrentReviewAttempt is not null) break;
                var candidate = _authority.GetTaskProjection(candidateKey!);
                if (candidate.CurrentRunAttempt is null && candidate.CurrentReviewAttempt is null) continue;
                taskKey = candidateKey!;
                projection = candidate;
            }
            // Pending, leased, terminal, and scheduled ReviewInfra attempts all
            // have canonical authority and must not be replaced by this sweep.
            if (projection.CurrentReviewAttempt is not null) continue;
            var run = projection.CurrentRunAttempt;
            if (run is not { State: AttemptLifecycleState.Completed, ResultSha: { Length: > 0 }, ResultEnvelope: not null })
                continue;

            var project = _projects.FindByStorageLocation(task.WatchPath)
                          ?? _projects.FindByIdOrDisplayName(task.ProjectName);
            var taskSettings = _settings.Get(task.ProjectName);
            var integrationRef = V1ReviewPlaneEndpoints
                .ResolveBaselineBranch(task, project, _settings).IntegrationRef;
            var repositoryPath = _git.ResolveRepoRootForWatchPath(task.WatchPath) ?? project?.RepositoryPath;
            var plan = _remoteReviewPlans.Build(task, repositoryPath, taskSettings, integrationRef);
            var requirementsPath = Path.Combine(task.FolderPath, "prompt.md");
            var requirements = File.Exists(requirementsPath) ? File.ReadAllText(requirementsPath) : task.Id;
            var result = _lifecycle.CreateReviewAttemptInAutoReview(task, new CreateReviewAttemptRequest(
                taskKey,
                run.RepositoryId,
                run.ResultSha,
                run.AttemptId,
                AttemptAuthorityService.Hash(requirements),
                AttemptAuthorityService.Hash("remote-review-policy:v1"),
                run.EvidenceDigests,
                $"missing-review-attempt:{run.AttemptId}",
                RepositoryUrl: run.ResultEnvelope.RepositoryUrl,
                ResultRef: run.ResultEnvelope.ImmutableRemoteRef,
                Plan: plan));
            if (!result.Accepted) continue;

            created++;
            _logger.LogWarning(
                "review-missing-attempt-recovered task={TaskKey} runAttempt={RunAttemptId} reviewAttempt={ReviewAttemptId} timeoutMinutes={TimeoutMinutes}",
                taskKey, run.AttemptId, result.AttemptId, timeoutMinutes);
        }
        return created;
    }

    private bool FireDue(ReviewInfrastructureRetryDue item)
    {
        var review = _authority.GetReview(item.AttemptId);
        if (review is null) return false;

        var task = V1ReviewPlaneEndpoints.FindTask(_scanner, review.TaskKey);
        if (task is null
            || !string.Equals(task.State, TaskStates.AutoReview, StringComparison.OrdinalIgnoreCase))
        {
            // The card left Auto Review (an operator moved it, or it was
            // resolved some other way) before the retry came due. The
            // schedule is moot, not failed - abandon it instead of spinning
            // on it forever.
            _authority.ClearScheduledReviewInfrastructureRetry(item.AttemptId);
            return false;
        }

        Contract.ReviewPlanDto? retryPlan = null;
        if (ReviewInfrastructureRetryPlanPolicy.RequiresRebuild(review.FailureClassification))
        {
            var project = _projects.FindByStorageLocation(task.WatchPath)
                          ?? _projects.FindByIdOrDisplayName(task.ProjectName);
            var taskSettings = _settings.Get(task.ProjectName);
            var integrationRef = V1ReviewPlaneEndpoints
                .ResolveBaselineBranch(task, project, _settings).IntegrationRef;
            var repositoryPath = _git.ResolveRepoRootForWatchPath(task.WatchPath) ?? project?.RepositoryPath;
            retryPlan = _remoteReviewPlans.Build(task, repositoryPath, taskSettings, integrationRef);
        }

        var created = _lifecycle.CreateReviewAttemptInAutoReview(task, new CreateReviewAttemptRequest(
            review.TaskKey,
            review.RepositoryId,
            review.Subject.ExpectedResultSha,
            review.SourceRunAttemptId,
            review.Subject.TaskRequirementsHash,
            review.Subject.ReviewPolicyHash,
            review.Subject.EvidenceDigestInputs,
            $"review-infra-retry:{item.AttemptId}",
            review.AttemptId,
            review.Subject.RepositoryUrl,
            review.Subject.ResultRef,
            retryPlan));

        if (!created.Accepted)
        {
            _logger.LogWarning(
                "review-infrastructure-retry-create-refused attempt={AttemptId} task={TaskKey} status={Status} message={Message}",
                item.AttemptId,
                item.TaskKey,
                created.Status,
                created.Message);
            return false;
        }

        _authority.ClearScheduledReviewInfrastructureRetry(item.AttemptId);
        _logger.LogInformation(
            "review-infrastructure-retry-fired attempt={AttemptId} successor={SuccessorId} task={TaskKey} "
            + "retryNumber={RetryNumber}/{RetryBudget}",
            item.AttemptId,
            created.AttemptId,
            item.TaskKey,
            item.RetryNumber,
            item.RetryBudget);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Runner:ReviewInfrastructureRetry:Enabled", true))
        {
            _logger.LogInformation(
                "ReviewInfrastructureRetryScheduler: disabled via Runner:ReviewInfrastructureRetry:Enabled=false");
            return;
        }

        var intervalSeconds = Math.Clamp(
            _configuration.GetValue<int?>("Runner:ReviewInfrastructureRetry:IntervalSeconds")
            ?? DefaultIntervalSeconds,
            5, 60);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                RunOnce();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "review-infrastructure-retry-scheduler-tick-failed");
            }
        }
    }
}
