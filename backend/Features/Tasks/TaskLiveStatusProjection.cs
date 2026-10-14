using System.Globalization;
using System.Text.Json;

using AgentStudio.Cli;
using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Runner;

namespace AgentStudio.Tasks;

/// <summary>
/// Builds the board/detail liveness strip from facts the runtime already
/// records. The projection reads only the root execution record, which is the
/// newest attempt; <see cref="PipelineExecutionRecord.PreviousAttempts"/> is
/// never consulted.
/// </summary>
public sealed class TaskLiveStatusProjection(
    PipelineExecutionLog pipelineLog,
    StepPromptLog promptLog,
    ProjectSettingsService projectSettings,
    TaskRunnerService runners,
    AutoReviewPostProcessingQueue reviewQueue,
    AttemptAuthorityService attemptAuthority)
{
    private static readonly HashSet<string> VisibleStates = new(StringComparer.OrdinalIgnoreCase)
    {
        TaskStates.Preparation,
        TaskStates.Ready,
        TaskStates.Progress,
        TaskStates.AutoReview,
    };

    public IReadOnlyDictionary<string, TaskLiveStatus> BuildLookup(IEnumerable<TaskInfo> jobs)
    {
        var jobList = jobs.Where(job => VisibleStates.Contains(job.State)).ToList();
        var runnerStatus = runners.GetStatus().Projects;
        var result = new Dictionary<string, TaskLiveStatus>(StringComparer.OrdinalIgnoreCase);
        // AGT-3094: both reads are the authority's immutable board snapshot, so
        // the review queue line stays lock-free and costs one read per call.
        BoardAuthoritySnapshot? authority = null;
        IReadOnlyDictionary<string, ReviewQueueEntry>? reviewQueueEntries = null;
        if (jobList.Any(job => job.State == TaskStates.AutoReview))
        {
            authority = attemptAuthority.ReadBoardAuthoritySnapshot();
            reviewQueueEntries = attemptAuthority.ReadReviewQueue();
        }

        foreach (var job in jobList)
        {
            var queue = ResolveQueue(job, runnerStatus, authority, reviewQueueEntries);
            var execution = pipelineLog.Read(job.FolderPath);
            var settings = PipelineTypeSettings.ForTask(projectSettings.Get(job.ProjectName), job)!;
            var pipeline = ProjectPipelineOrder.Apply(
                UiTaskPipelineRouter.Select(job, settings),
                settings);
            // The prompt log (.metadata/prompts.jsonl) holds full prompt texts
            // and is only consulted when a running step actually exists. Pass it
            // as a lazy factory so a poll over Ready/idle cards never parses it.
            result[job.TaskKey] = Build(
                job,
                pipeline,
                execution,
                settings,
                () => promptLog.ReadForJob(job.FolderPath),
                queue);
        }

        return result;
    }

    internal static TaskLiveStatus Build(
        TaskInfo job,
        TaskPipeline pipeline,
        PipelineExecutionRecord? execution,
        ProjectSettings settings,
        Func<IReadOnlyList<StepPromptEntry>> prompts,
        TaskLiveQueue? queue)
    {
        var definitions = pipeline.AllSteps
            .Where(step => !step.Stub && PipelineStepConfigResolver.IsEnabled(settings, step))
            .ToList();
        var byId = definitions.ToDictionary(step => step.Id, StringComparer.OrdinalIgnoreCase);
        // A Ready task is queued for a fresh run. Its root record still
        // describes the just-finished attempt until the next execution starts,
        // so treating those steps as current would suppress the real upcoming
        // chain and could resurrect a stale running marker.
        var awaitingFreshAttempt = string.Equals(job.State, TaskStates.Ready, StringComparison.OrdinalIgnoreCase);
        var records = awaitingFreshAttempt ? [] : execution?.Steps ?? [];

        var running = records
            .Where(step => step.Status == PipelineStepStatus.Running && byId.ContainsKey(step.StepId))
            .OrderByDescending(step => definitions.FindIndex(def =>
                string.Equals(def.Id, step.StepId, StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(step => step.StartedAt)
            .FirstOrDefault();

        TaskLiveStep? active = null;
        if (running is not null)
        {
            var definition = byId[running.StepId];
            var prompt = prompts()
                .Where(entry => string.Equals(entry.StepId, running.StepId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.At)
                .FirstOrDefault();
            active = new TaskLiveStep
            {
                StepId = running.StepId,
                DisplayName = definition.DisplayName,
                Kind = running.Kind.ToString().ToLowerInvariant(),
                StartedAt = running.StartedAt,
                Model = NonBlank(running.Model) ?? NonBlank(prompt?.Model) ?? NonBlank(definition.Model),
                CliType = running.Kind == StepKind.Core
                    ? NonBlank(job.CliType)
                    : NonBlank(prompt?.Cli) ?? NonBlank(definition.CliType),
            };
        }

        var statuses = records.ToDictionary(step => step.StepId, step => step.Status, StringComparer.OrdinalIgnoreCase);
        var activeIndex = active is null
            ? -1
            : definitions.FindIndex(step => string.Equals(step.Id, active.StepId, StringComparison.OrdinalIgnoreCase));
        var upcoming = definitions
            .Select((step, index) => (Step: step, Index: index))
            .Where(item => item.Index > activeIndex)
            .Where(item => !statuses.TryGetValue(item.Step.Id, out var status)
                || status is PipelineStepStatus.Pending)
            .Take(3)
            .Select(item => new TaskLiveStepPreview
            {
                StepId = item.Step.Id,
                DisplayName = item.Step.DisplayName,
            })
            .ToList();

        var latestStepEvent = records
            .SelectMany(step => new DateTime?[] { step.CompletedAt, step.StartedAt })
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty()
            .Max();
        DateTime? latestEvent = latestStepEvent == default ? null : latestStepEvent;
        latestEvent = Max(latestEvent, execution?.StartedAt, job.LastActivity == default ? null : job.LastActivity);

        return new TaskLiveStatus
        {
            Attempt = Math.Max(1, (execution?.Attempt ?? 1) + (awaitingFreshAttempt ? 1 : 0)),
            ActiveStep = active,
            NextSteps = upcoming,
            Queue = active is null ? queue : null,
            LatestEventAt = latestEvent,
        };
    }

    private TaskLiveQueue? ResolveQueue(
        TaskInfo job,
        IReadOnlyDictionary<string, ProjectRunnerStatus> runnerStatus,
        BoardAuthoritySnapshot? authority,
        IReadOnlyDictionary<string, ReviewQueueEntry>? reviewQueueEntries)
    {
        if (job.State == TaskStates.Ready
            && runnerStatus.TryGetValue(job.ProjectName, out var project))
        {
            var index = project.QueuedJobIds.FindIndex(id =>
                string.Equals(id, job.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) return new TaskLiveQueue { Kind = "runner", Position = index + 1 };
        }

        if (job.State == TaskStates.AutoReview)
        {
            var authorityKey = CanonicalAuthorityKey(job, authority);
            BoardReviewActivity? current = null;
            ReviewQueueEntry? queued = null;
            if (authorityKey is not null)
            {
                if (authority!.Reviews.TryGetValue(authorityKey, out var activity)) current = activity;
                if (reviewQueueEntries?.TryGetValue(authorityKey, out var entry) == true) queued = entry;
            }
            return ReviewQueueFor(
                authorityKey is not null,
                current,
                queued,
                reviewQueue.PositionOf(job.ProjectName, job.Id),
                reviewQueue.WaitStateOf(job.ProjectName, job.Id),
                DateTime.UtcNow);
        }

        return null;
    }

    /// <summary>
    /// The key a canonical card's ReviewAttempt is filed under, or null for a
    /// legacy or report-only card. Mirrors the rule the post-processing queue
    /// uses (<c>ReviewDecisionOrchestrator</c>): report-only modes never get a
    /// ReviewAttempt, and the first card identity the authority knows wins.
    /// </summary>
    internal static string? CanonicalAuthorityKey(TaskInfo job, BoardAuthoritySnapshot? authority)
    {
        if (authority is null || TaskModes.IsReportOnly(job.Mode)) return null;
        return new[] { TaskIdentity.AuthorityKey(job), job.TaskKey, job.Id }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .FirstOrDefault(value => authority.KnownTaskKeys.Contains(value.Trim()))
            ?.Trim();
    }

    /// <summary>
    /// AGT-3094: the queue line of an Auto Review card. A canonical card is
    /// described from its current ReviewAttempt in the authority snapshot, so
    /// a leased review names its actual lease holder and a review that nothing
    /// will pick up again reads as stalled instead of as a normal wait. Legacy
    /// cards, and a canonical card whose review already passed and now waits
    /// on integration, keep the local post-processing wait (AGT-2842, AGT-2860).
    /// Pure, so every state is pinned directly.
    /// </summary>
    internal static TaskLiveQueue? ReviewQueueFor(
        bool canonical,
        BoardReviewActivity? current,
        ReviewQueueEntry? queued,
        int? localPosition,
        AutoReviewQueueWaitState? localWait,
        DateTime nowUtc)
    {
        if (!canonical) return LocalReviewQueue(localPosition, localWait);

        // A priority-only entry reads as Completed but has no attempt behind it.
        if (current is not { AttemptId: not null } review)
            return Stalled("no review attempt for this delivery; operator decision needed", outcome: null);

        var leaseLive = review.LeaseExpiresAt is { } expires && expires > nowUtc;
        if (review.State == AttemptLifecycleState.Leased && leaseLive)
        {
            var executor = string.IsNullOrWhiteSpace(review.ExecutorId) ? "an unnamed executor" : review.ExecutorId;
            return new TaskLiveQueue
            {
                Kind = "review",
                State = TaskLiveQueueStates.Reviewing,
                ExecutorId = review.ExecutorId,
                Since = review.AcquiredAt,
                Reason = review.AcquiredAt is { } since
                    ? $"reviewing on {executor} since {Utc(since)}"
                    : $"reviewing on {executor}",
            };
        }

        if (review.State is AttemptLifecycleState.Pending or AttemptLifecycleState.Leased)
        {
            // Pending, or a lease that ran out and waits to be re-claimed: the
            // same set ReadReviewQueue numbers. Only a missing executor
            // registration is worth a sentence; the position says the rest.
            var registrationWait = localWait is not null
                && localWait.Reason == PostProcessingCardResult.AwaitingReviewExecutorRegistration;
            return new TaskLiveQueue
            {
                Kind = "review",
                Position = queued?.Position,
                Reason = registrationWait ? DescribeWait(localWait!)
                    : queued?.Position is null ? "review attempt pending" : null,
            };
        }

        if (review.Outcome is ReviewTerminalOutcome.Pass or ReviewTerminalOutcome.IntegrationBranchDefect)
            return LocalReviewQueue(localPosition, localWait);

        var outcome = OutcomeToken(review);
        if (review.PendingInfrastructureRetryAt is { } retryAt)
        {
            return new TaskLiveQueue
            {
                Kind = "review",
                Outcome = outcome,
                Reason = $"review ended ({outcome}); retry scheduled for {Utc(retryAt)}",
            };
        }

        return Stalled($"review ended ({outcome}); no new review attempt; operator decision needed", outcome);
    }

    private static TaskLiveQueue? LocalReviewQueue(int? localPosition, AutoReviewQueueWaitState? localWait)
    {
        if (localPosition.HasValue)
            return new TaskLiveQueue { Kind = "review", Position = localPosition.Value };

        // Between two post-processing passes a deferred card has left the
        // local slot queue (it has no Position) but is not actually idle -
        // it is waiting out its backoff. Naming that wait keeps the card from
        // reading as an unexplained empty queue (AGT-2842).
        return localWait is null ? null : new TaskLiveQueue { Kind = "review", Reason = DescribeWait(localWait) };
    }

    private static TaskLiveQueue Stalled(string reason, string? outcome) => new()
    {
        Kind = "review",
        State = TaskLiveQueueStates.Stalled,
        Outcome = outcome,
        Reason = reason,
    };

    private static string OutcomeToken(BoardReviewActivity review)
        => JsonNamingPolicy.CamelCase.ConvertName(
            review.Outcome?.ToString() ?? review.State.ToString());

    private static string Utc(DateTime value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Says what the card is actually waiting for. The four canonical waits used
    /// to share one reason token and therefore one sentence about the executor,
    /// which was wrong for a card whose review had already passed (AGT-2860).
    /// </summary>
    private static string DescribeWait(AutoReviewQueueWaitState wait)
    {
        var headline = wait.Reason switch
        {
            PostProcessingCardResult.AwaitingReviewExecutorRegistration => "waiting for review executor",
            PostProcessingCardResult.AwaitingCanonicalReviewVerdict => "waiting for review executor",
            PostProcessingCardResult.AwaitingDeliveryIntegration => "waiting for delivery integration",
            PostProcessingCardResult.AwaitingIntegrationCompletion => "waiting to leave auto review",
            "delivery-resume-exhausted" => "delivery resume stopped; operator action needed",
            "delivery-resume-backoff" => "delivery resume will retry",
            AutoReviewDeliveryResumeService.Reasons.IntegrationOwnedWait => "waiting for integration recovery",
            _ => null,
        };
        if (headline is null) return $"waiting to retry ({wait.Reason})";
        return string.IsNullOrWhiteSpace(wait.Detail) ? headline : $"{headline}: {wait.Detail}";
    }

    private static string? NonBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime? Max(params DateTime?[] values)
    {
        var present = values.Where(value => value.HasValue).Select(value => value!.Value).ToList();
        return present.Count == 0 ? null : present.Max();
    }
}
