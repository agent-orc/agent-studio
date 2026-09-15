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
    AutoReviewPostProcessingQueue reviewQueue)
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

        foreach (var job in jobList)
        {
            var queue = ResolveQueue(job, runnerStatus);
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
        IReadOnlyDictionary<string, ProjectRunnerStatus> runnerStatus)
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
            var position = reviewQueue.PositionOf(job.ProjectName, job.Id);
            if (position.HasValue)
                return new TaskLiveQueue { Kind = "review", Position = position.Value };

            // Between two post-processing passes a deferred card has left the
            // local slot queue (it has no Position) but is not actually idle -
            // it is waiting out its backoff, most commonly for the canonical
            // remote review executor to claim it. Naming that wait keeps the
            // card from reading as an unexplained empty queue (AGT-2842).
            var wait = reviewQueue.WaitStateOf(job.ProjectName, job.Id);
            if (wait is not null)
                return new TaskLiveQueue { Kind = "review", Reason = DescribeWait(wait) };
        }

        return null;
    }

    private static string DescribeWait(AutoReviewQueueWaitState wait)
    {
        if (string.Equals(
                wait.Reason, PostProcessingCardResult.AwaitingCanonicalReviewExecutor, StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(wait.Detail)
                ? "waiting for review executor"
                : $"waiting for review executor: {wait.Detail}";
        }
        return $"waiting to retry ({wait.Reason})";
    }

    private static string? NonBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime? Max(params DateTime?[] values)
    {
        var present = values.Where(value => value.HasValue).Select(value => value!.Value).ToList();
        return present.Count == 0 ? null : present.Max();
    }
}
