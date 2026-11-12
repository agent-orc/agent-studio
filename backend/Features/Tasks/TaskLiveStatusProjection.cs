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
    AttemptAuthorityService attemptAuthority,
    V1ReviewExecutorRegistry reviewExecutors)
{
    private static readonly HashSet<string> VisibleStates = new(StringComparer.OrdinalIgnoreCase)
    {
        TaskStates.Preparation,
        TaskStates.Ready,
        TaskStates.Progress,
        TaskStates.AutoReview,
    };

    public IReadOnlyDictionary<string, TaskLiveStatus> BuildLookup(IEnumerable<TaskInfo> jobs,
        bool publishedRunnerStatus = false)
    {
        var jobList = jobs.Where(job => VisibleStates.Contains(job.State)).ToList();
        var runnerStatus = (publishedRunnerStatus
            ? runners.GetPublishedStatus() : runners.GetStatus()).Projects;
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
        var reviewHost = authority is null ? null : reviewExecutors.LatestReviewHostTelemetry(DateTime.UtcNow);

        foreach (var job in jobList)
        {
            var queue = ResolveQueue(job, runnerStatus, authority, reviewQueueEntries, reviewHost);
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
            .Where(item => job.State != TaskStates.AutoReview || item.Step.Kind != StepKind.Core)
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
        IReadOnlyDictionary<string, ReviewQueueEntry>? reviewQueueEntries,
        AgentStudio.TaskServer.Contracts.HostTelemetrySnapshotDto? reviewHost)
    {
        if (job.State == TaskStates.Ready
            && runnerStatus.TryGetValue(job.ProjectName, out var project))
        {
            var runnerQueue = RunnerQueueFor(job.Id, project);
            if (runnerQueue is not null) return runnerQueue;
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
                DateTime.UtcNow,
                reviewHost);
        }

        return null;
    }

    /// <summary>
    /// The runner queue line of a Ready card. Only a pickup candidate (a card
    /// in <see cref="ProjectRunnerStatus.QueuedJobIds"/>) gets one; a held card
    /// states its hold instead. AGT-3579: the runner gate outranks a candidate
    /// skip, because while the gate is closed no candidate is evaluated.
    /// </summary>
    internal static TaskLiveQueue? RunnerQueueFor(string jobId, ProjectRunnerStatus project)
    {
        var index = project.QueuedJobIds.FindIndex(id =>
            string.Equals(id, jobId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;

        var queue = new TaskLiveQueue { Kind = "runner", Position = index + 1 };
        // A mode change is visible before the next pickup tick records its gate.
        // Remote claims obey the same project mode, so a just queued Run now
        // must not look as though it is waiting for an occupied slot.
        // The breaker changes mode synchronously; its pickup gate is published
        // on the next tick. Use the breaker snapshot so that interval cannot
        // advertise a manual-mode override of the safety cooldown.
        if (project.BreakerCooldownUntil is { } until)
            return queue with { ReasonCode = RunnerPickupWaitCodes.BreakerCooldown,
                Reason = $"Auto-pickup is cooling down after a circuit-breaker trip ({project.BreakerReason ?? "unknown cause"}) and resumes at {until:O}." };
        if (project.Mode == "manual")
            return queue with { ReasonCode = RunnerPickupWaitCodes.ManualMode,
                Reason = project.PickupGate is { Code: RunnerPickupWaitCodes.ManualMode } manualGate
                    ? manualGate.Detail
                    : "Auto-pickup is off; switch the project to auto to let its runner claim the card." };
        if (project.Mode == "paused")
            return queue with { ReasonCode = RunnerPickupWaitCodes.Paused,
                Reason = project.PickupGate is { Code: RunnerPickupWaitCodes.Paused } pausedGate
                    ? pausedGate.Detail
                    : "The runner is paused; no card is picked until auto-pickup is enabled again." };
        if (project.PickupGate is { } gate)
            return queue with { ReasonCode = gate.Code, Reason = gate.Detail };
        var skip = project.PickupSkips.FirstOrDefault(item =>
            string.Equals(item.JobId, jobId, StringComparison.OrdinalIgnoreCase));
        return skip is null ? queue : queue with { ReasonCode = skip.Code, Reason = skip.Detail };
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
        DateTime nowUtc,
        AgentStudio.TaskServer.Contracts.HostTelemetrySnapshotDto? host = null)
    {
        if (!canonical)
        {
            if (!IsIntegrationWait(localWait)
                && (localPosition.HasValue || localWait is not null)
                && ReviewHostHold(host) is { } hostHold)
                return new TaskLiveQueue { Kind = "review", Reason = hostHold };
            return LocalReviewQueue(localPosition, localWait);
        }

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
            if (ReviewHostHold(host) is { } hostHold)
                return new TaskLiveQueue { Kind = "review", Reason = hostHold };
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
            return LocalReviewQueue(localPosition, localWait, postReview: true)
                ?? new TaskLiveQueue { Kind = "review", State = TaskLiveQueueStates.Integration,
                    Reason = "waiting for delivery integration" };

        var outcome = OutcomeToken(review);
        // AGT-3690: an infrastructure failure names its classification and the
        // host it happened on. The outcome enum alone ("infrastructureFailure")
        // hid that the review host had restarted, and the retry it scheduled
        // was visible only as a timeline row.
        var lost = LossOf(review);
        if (review.PendingInfrastructureRetryAt is { } retryAt)
        {
            if (lost is null)
            {
                return new TaskLiveQueue
                {
                    Kind = "review",
                    Outcome = outcome,
                    Reason = $"review ended ({outcome}); retry scheduled for {Utc(retryAt)}",
                };
            }
            var retry = new TaskLiveReviewRetry
            {
                Number = review.PendingInfrastructureRetryNumber,
                Budget = AttemptAuthorityService.ReviewInfrastructureRetryBudget,
                DueAt = retryAt,
            };
            return new TaskLiveQueue
            {
                Kind = "review",
                State = TaskLiveQueueStates.RetryScheduled,
                Outcome = outcome,
                Lost = lost,
                Retry = retry,
                Reason = $"{LossSentence(lost)} {RetrySentence(retry)}",
            };
        }

        return lost is null
            ? Stalled($"review ended ({outcome}); no new review attempt; operator decision needed", outcome)
            : Stalled($"{LossSentence(lost)} No automatic retry is scheduled; operator decision needed.", outcome) with
            {
                Lost = lost,
            };
    }

    internal static string? ReviewHostHold(AgentStudio.TaskServer.Contracts.HostTelemetrySnapshotDto? host)
    {
        if (host is null) return null;
        var cores = host.ReviewPlane?.HostCores ?? host.CpuCores;
        if (host.Load1 is { } load && cores > 0 && load / cores >= 1.0)
            return $"Review host saturated: load/core {load / cores:0.0} > 1.0";
        if (host.ReviewPlane is { PlaneCpuCores: < 2.0 } plane)
            return $"Review host has no review capacity: {plane.PlaneCpuCores:0.0} cores available; raise the review role quota or wait for capacity.";
        if (host.ReviewPlane is { CurrentCeiling: <= 0 })
            return "Review host has no review slots available; raise the review ceiling or wait for capacity.";
        if (host.ReviewPlane is { CurrentCeiling: > 0 } capacity
            && host.ActiveSlots >= capacity.CurrentCeiling)
            return $"Review host at capacity: {host.ActiveSlots}/{capacity.CurrentCeiling} slots in use; wait for a slot.";
        return null;
    }

    private static TaskLiveReviewLoss? LossOf(BoardReviewActivity review)
    {
        if (review.Outcome != ReviewTerminalOutcome.InfrastructureFailure) return null;
        var classification = string.IsNullOrWhiteSpace(review.FailureClassification)
            ? null
            : review.FailureClassification.Trim();
        return new TaskLiveReviewLoss
        {
            Cause = string.Equals(
                    classification, ReviewAttemptChainEntry.ExecutorRestartedClassification,
                    StringComparison.OrdinalIgnoreCase)
                ? TaskLiveReviewLossCauses.HostRestarted
                : TaskLiveReviewLossCauses.Infrastructure,
            Classification = classification,
            HostId = review.HostId,
            ExecutorId = review.ExecutorId,
            At = review.TerminalAt,
        };
    }

    private static string LossSentence(TaskLiveReviewLoss lost)
    {
        var where = string.Join(", ", new[]
        {
            lost.HostId ?? lost.ExecutorId,
            lost.At is { } at ? Utc(at) : null,
        }.Where(part => !string.IsNullOrWhiteSpace(part)));
        var cause = lost.Cause == TaskLiveReviewLossCauses.HostRestarted
            ? "review host restarted"
            : lost.Classification is null
                ? "review infrastructure failed"
                : $"review infrastructure failed ({lost.Classification})";
        return where.Length == 0 ? $"Review lost: {cause}." : $"Review lost: {cause} ({where}).";
    }

    private static string RetrySentence(TaskLiveReviewRetry retry)
        => retry.Number is { } number
            ? $"Automatic retry {number} of {retry.Budget} at {Utc(retry.DueAt)}."
            : $"Automatic retry at {Utc(retry.DueAt)}.";

    private static TaskLiveQueue? LocalReviewQueue(int? localPosition, AutoReviewQueueWaitState? localWait,
        bool postReview = false)
    {
        var state = postReview || IsIntegrationWait(localWait)
            ? TaskLiveQueueStates.Integration : TaskLiveQueueStates.Queued;
        if (localPosition.HasValue)
            return new TaskLiveQueue { Kind = "review", Position = localPosition.Value, State = state };

        // Between two post-processing passes a deferred card has left the
        // local slot queue (it has no Position) but is not actually idle -
        // it is waiting out its backoff. Naming that wait keeps the card from
        // reading as an unexplained empty queue (AGT-2842).
        return localWait is null ? null : new TaskLiveQueue
            { Kind = "review", State = state, Reason = DescribeWait(localWait) };
    }

    private static bool IsIntegrationWait(AutoReviewQueueWaitState? wait) => wait?.Reason is
        PostProcessingCardResult.AwaitingDeliveryIntegration
        or PostProcessingCardResult.AwaitingIntegrationCompletion
        or "delivery-resume-exhausted"
        or "delivery-resume-backoff"
        or AutoReviewDeliveryResumeService.Reasons.IntegrationOwnedWait;

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
