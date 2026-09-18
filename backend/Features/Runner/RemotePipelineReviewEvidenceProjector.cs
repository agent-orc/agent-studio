using System.Text;
using AgentStudio.GeneratedFiles;
using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// Projects a fenced Remote Review report into the same task-local pipeline,
/// aspect, file-provenance, and timeline views used by local pipeline steps.
/// The ReviewAttempt remains the authority. These files are operator-facing
/// evidence and never decide admission or lease ownership.
/// </summary>
public sealed class RemotePipelineReviewEvidenceProjector
{
    private readonly PipelineExecutionLog _pipeline;
    private readonly TimelineLog _timeline;
    private readonly FileGenerationIndex _files;
    private readonly AgentStudio.Projects.ProjectSettingsService _settings;

    public RemotePipelineReviewEvidenceProjector(
        PipelineExecutionLog pipeline,
        TimelineLog timeline,
        FileGenerationIndex files,
        AgentStudio.Projects.ProjectSettingsService settings)
    {
        _pipeline = pipeline;
        _timeline = timeline;
        _files = files;
        _settings = settings;
    }

    public async Task ProjectAsync(
        TaskInfo task,
        ReviewAttemptDto review,
        Contract.ReviewReportRequest report,
        string evidenceFile,
        DateTime receivedAt,
        CancellationToken ct)
    {
        var settings = PipelineTypeSettings.ForTask(_settings.Get(task.ProjectName), task);
        var catalogue = ProjectPipelineOrder.Apply(PipelineCatalogue.ForTask(task), settings);
        var execution = _pipeline.EnsureRun(
            task.FolderPath,
            catalogue,
            task.ProjectName,
            task.Id,
            report.Commands.Select(command => command.StartedAt).DefaultIfEmpty(receivedAt).Min());
        using var attempt = _pipeline.EnterAttempt(task.FolderPath, execution.Attempt);

        foreach (var command in report.Commands.Where(command => command.Phase == "verification"))
        {
            if (Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind))
                await ProjectAspectAsync(task, review, command, report, receivedAt, execution.Attempt, ct);
        }
        foreach (var verdict in report.Verdicts.Where(verdict =>
                     !string.IsNullOrWhiteSpace(verdict.CarriedOverFrom)))
        {
            ProjectCarriedAspect(task, review, verdict, receivedAt, execution.Attempt);
        }
        ProjectToolGate(task, review, report, execution.Attempt);
        ProjectTimeline(task, review, report, evidenceFile, receivedAt);
    }

    private void ProjectCarriedAspect(
        TaskInfo task,
        ReviewAttemptDto review,
        Contract.ReviewVerdictDto verdict,
        DateTime receivedAt,
        int pipelineAttempt)
    {
        var stepId = verdict.Aspect.StartsWith("aspect-", StringComparison.OrdinalIgnoreCase)
            ? verdict.Aspect
            : $"aspect-{verdict.Aspect}";
        var passed = string.Equals(verdict.Status, "pass", StringComparison.OrdinalIgnoreCase);
        _pipeline.RecordStep(task.FolderPath, new PipelineStepExecution
        {
            StepId = stepId,
            Kind = StepKind.Aspect,
            Attempt = pipelineAttempt,
            Status = passed ? PipelineStepStatus.Passed : PipelineStepStatus.Failed,
            StartedAt = receivedAt,
            CompletedAt = receivedAt,
            DurationMs = 0,
            Reason = verdict.Summary,
            Verdict = verdict.Status,
            VerdictSummary = verdict.Summary,
            CarriedOverFrom = verdict.CarriedOverFrom,
            StillOpen = !passed,
            ExecutionLocation = "carried-over",
            ExecutionAttemptId = review.AttemptId,
        });
    }

    private async Task ProjectAspectAsync(
        TaskInfo task,
        ReviewAttemptDto review,
        Contract.ReviewCommandEvidenceDto command,
        Contract.ReviewReportRequest report,
        DateTime receivedAt,
        int pipelineAttempt,
        CancellationToken ct)
    {
        var aspectId = command.StepId.StartsWith("aspect-", StringComparison.OrdinalIgnoreCase)
            ? command.StepId["aspect-".Length..]
            : command.Aspect;
        if (!AspectRunnerService.Catalogue.TryGetValue(aspectId, out var definition))
            return;
        var remoteVerdict = report.Verdicts.LastOrDefault(verdict =>
            string.Equals(verdict.Aspect, command.Aspect, StringComparison.OrdinalIgnoreCase));
        var status = remoteVerdict?.Status.ToLowerInvariant() switch
        {
            "pass" => AspectStatus.Pass,
            "block" or "fail" => AspectStatus.Block,
            _ => AspectStatus.Concerns,
        };
        var response = ArtifactText(report.Artifacts, command.StdoutSha256);
        var summary = remoteVerdict?.Summary
                      ?? $"Remote aspect '{aspectId}' returned no structured summary.";
        var concernRound = ReviewConcernRoundStore.Read(task.FolderPath);
        var causedConcernRound = concernRound?.AspectIds.Contains(
            aspectId,
            StringComparer.OrdinalIgnoreCase) == true;
        var verdict = new AspectVerdict(
            aspectId,
            status,
            summary,
            string.IsNullOrWhiteSpace(response)
                ? $"_{summary}_\n"
                : $"## Model reply\n\n```\n{response.Trim()}\n```\n",
            status == AspectStatus.Pass ? null : $"{definition.ConcernNamespace}:concerns");
        var markdownName = $"aspect-{aspectId}.md";
        var jsonName = $"aspect-{aspectId}.json";
        await File.WriteAllTextAsync(
            Path.Combine(task.FolderPath, markdownName),
            AspectVerdictParsing.RenderReport(verdict, receivedAt),
            new UTF8Encoding(false),
            ct);
        await File.WriteAllTextAsync(
            Path.Combine(task.FolderPath, jsonName),
            AspectVerdictParsing.RenderJson(verdict, command.Model, receivedAt),
            new UTF8Encoding(false),
            ct);

        var duration = Math.Max(0, (long)(command.FinishedAt - command.StartedAt).TotalMilliseconds);
        var generation = new FileGenerationMeta
        {
            Kind = "aspect",
            Model = command.Model,
            Cli = command.FileName,
            TokensIn = command.InputTokens,
            TokensOut = command.OutputTokens,
            CacheReadTokens = command.CacheReadTokens,
            CacheCreationTokens = command.CacheCreationTokens,
            StartedAt = command.StartedAt,
            EndedAt = command.FinishedAt,
            DurationMs = duration,
            StepId = command.StepId,
            HeadShaAfter = command.ExpectedResultSha,
        };
        _files.Upsert(task.FolderPath, generation with { File = markdownName });
        _files.Upsert(task.FolderPath, generation with { File = jsonName });
        _pipeline.RecordStep(task.FolderPath, new PipelineStepExecution
        {
            StepId = command.StepId,
            Kind = StepKind.Aspect,
            Attempt = pipelineAttempt,
            Model = command.Model,
            ThinkingLevel = command.ThinkingLevel,
            Status = status == AspectStatus.Pass
                ? PipelineStepStatus.Passed
                : PipelineStepStatus.Failed,
            StartedAt = command.StartedAt,
            CompletedAt = command.FinishedAt,
            DurationMs = duration,
            InputTokens = command.InputTokens,
            OutputTokens = command.OutputTokens,
            CacheReadTokens = command.CacheReadTokens,
            CacheCreationTokens = command.CacheCreationTokens,
            Reason = summary,
            FixedInRun = causedConcernRound && status == AspectStatus.Pass
                ? concernRound?.FixRunIndex
                : null,
            StillOpen = causedConcernRound && status != AspectStatus.Pass,
            ExecutionLocation = "remote",
            ExecutionHostId = review.Lease?.HostId ?? report.Environment.HostId,
            ExecutionExecutorId = review.Lease?.ExecutorId ?? report.ExecutorId,
            ExecutionAttemptId = review.AttemptId,
        });
    }

    private void ProjectToolGate(
        TaskInfo task,
        ReviewAttemptDto review,
        Contract.ReviewReportRequest report,
        int pipelineAttempt)
    {
        var tools = report.Commands
            .Where(command => command.Phase == "verification"
                              && !Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind))
            .ToArray();
        if (tools.Length == 0) return;
        var passed = tools.All(command => command.ExitCode == 0 && command.Signal is null);
        var first = tools.MinBy(command => command.StartedAt)!;
        var last = tools.MaxBy(command => command.FinishedAt)!;
        _pipeline.RecordStep(task.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.BuildTestGateStepId,
            Kind = StepKind.Tool,
            Attempt = pipelineAttempt,
            Status = passed ? PipelineStepStatus.Passed : PipelineStepStatus.Failed,
            StartedAt = first.StartedAt,
            CompletedAt = last.FinishedAt,
            DurationMs = Math.Max(0, (long)(last.FinishedAt - first.StartedAt).TotalMilliseconds),
            Reason = passed
                ? $"{tools.Length} remote tool gate command(s) passed at the immutable Result-SHA."
                : "A remote tool gate command failed at the immutable Result-SHA.",
            ExecutionLocation = "remote",
            ExecutionHostId = review.Lease?.HostId ?? report.Environment.HostId,
            ExecutionExecutorId = review.Lease?.ExecutorId ?? report.ExecutorId,
            ExecutionAttemptId = review.AttemptId,
        });
    }

    private void ProjectTimeline(
        TaskInfo task,
        ReviewAttemptDto review,
        Contract.ReviewReportRequest report,
        string evidenceFile,
        DateTime receivedAt)
    {
        var existing = _timeline.ReadAll(task.FolderPath)
            .Where(item => string.Equals(item.RunId, review.AttemptId, StringComparison.Ordinal))
            .Select(item => $"{item.Kind}:{Detail(item, "stepId")}")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var command in report.Commands)
        {
            var details = Details(review, report, command);
            var startedKey = $"{TimelineEventKinds.PostStepStarted}:{command.StepId}";
            if (existing.Add(startedKey))
            {
                _timeline.Append(task.FolderPath, new TimelineEvent
                {
                    Ts = command.StartedAt,
                    Kind = TimelineEventKinds.PostStepStarted,
                    Actor = TimelineActors.External,
                    Summary = $"Remote pipeline step started: {command.StepId}",
                    RunId = review.AttemptId,
                    PayloadRef = evidenceFile,
                    Details = details,
                });
            }

            var finishedKey = $"{TimelineEventKinds.PostStepFinished}:{command.StepId}";
            if (!existing.Add(finishedKey)) continue;
            _timeline.Append(task.FolderPath, new TimelineEvent
            {
                Ts = command.FinishedAt,
                Kind = TimelineEventKinds.PostStepFinished,
                Actor = TimelineActors.External,
                Summary = $"Remote pipeline step finished: {command.StepId}",
                RunId = review.AttemptId,
                PayloadRef = evidenceFile,
                Details = details,
            });
        }

        ProjectSupersededWorkerRelease(task, review, report, evidenceFile, receivedAt, existing);
    }

    /// <summary>
    /// AGT-2863: name the build that actually graded this attempt when it is not
    /// the one the reporting daemon runs. A review-daemon restart adopts running
    /// workers rather than discarding their gate work, so a verdict can come from
    /// a superseded release. The verdict stands; the card just stops hiding it.
    /// </summary>
    private void ProjectSupersededWorkerRelease(
        TaskInfo task,
        ReviewAttemptDto review,
        Contract.ReviewReportRequest report,
        string evidenceFile,
        DateTime receivedAt,
        HashSet<string> existing)
    {
        var worker = report.Environment.Worker;
        if (Contract.ReviewWorkerProvenancePolicy.SupersededNotice(worker) is not { } notice) return;
        if (!existing.Add($"{TimelineEventKinds.ReviewGradedBySupersededRelease}:")) return;

        _timeline.Append(task.FolderPath, new TimelineEvent
        {
            Ts = receivedAt,
            Kind = TimelineEventKinds.ReviewGradedBySupersededRelease,
            Actor = TimelineActors.External,
            Summary = $"Remote review {notice}",
            RunId = review.AttemptId,
            PayloadRef = evidenceFile,
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["attemptId"] = review.AttemptId,
                ["workerReleaseId"] = worker!.WorkerReleaseId,
                ["daemonReleaseId"] = worker.DaemonReleaseId,
                ["workerBinaryPath"] = worker.WorkerBinaryPath,
                ["fence"] = report.Fence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        });
    }

    private static Dictionary<string, string> Details(
        ReviewAttemptDto review,
        Contract.ReviewReportRequest report,
        Contract.ReviewCommandEvidenceDto command)
    {
        var hostId = review.Lease?.HostId ?? report.Environment.HostId;
        var executorId = review.Lease?.ExecutorId ?? report.ExecutorId;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stepId"] = command.StepId,
            ["pipelineStepId"] = Contract.ReviewCommandKinds.IsAgent(command.ExecutionKind)
                ? command.StepId
                : PipelineCatalogue.BuildTestGateStepId,
            ["executionKind"] = command.ExecutionKind,
            ["executionLocation"] = "remote",
            ["hostId"] = hostId,
            ["executorId"] = executorId,
            ["attemptId"] = review.AttemptId,
            ["leaseId"] = report.LeaseId,
            ["fence"] = report.Fence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["resultSha"] = command.ExpectedResultSha,
            ["phase"] = command.Phase,
            ["status"] = command.ExitCode == 0 && command.Signal is null ? "passed" : "failed",
            ["durationMs"] = Math.Max(0, (long)(command.FinishedAt - command.StartedAt).TotalMilliseconds)
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["model"] = command.Model ?? string.Empty,
            ["thinkingLevel"] = command.ThinkingLevel ?? string.Empty,
        };
    }

    private static string ArtifactText(
        IReadOnlyList<Contract.ReviewArtifactEvidenceDto> artifacts,
        string digest)
    {
        var artifact = artifacts.LastOrDefault(item =>
            string.Equals(item.Sha256, digest, StringComparison.OrdinalIgnoreCase));
        if (artifact?.ContentBase64 is null) return string.Empty;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(artifact.ContentBase64));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    private static string Detail(TimelineEvent item, string key)
        => item.Details is not null && item.Details.TryGetValue(key, out var value)
            ? value
            : string.Empty;
}
