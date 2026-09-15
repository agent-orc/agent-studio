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
