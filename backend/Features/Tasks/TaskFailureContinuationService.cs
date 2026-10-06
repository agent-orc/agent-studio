namespace AgentStudio.Tasks;

/// <summary>Why a failure continuation did or did not start.</summary>
/// <param name="Status">HTTP-shaped status: 202 started, queued, or saved; 404 or 409 refused; or the runner's error status.</param>
/// <param name="Error">Operator-facing refusal text, null when the continuation was queued.</param>
/// <param name="RunStatus">The runner's <c>started</c>, <c>queued</c>, or <c>saved</c> answer.</param>
/// <param name="Stage">The failed stage the continuation addresses.</param>
public sealed record TaskFailureContinuationResult(
    int Status,
    string? Error,
    string? RunStatus = null,
    string? Stage = null,
    string? TaskKey = null,
    string? SavedReason = null)
{
    public bool Accepted => Error is null;
    public bool Started => Accepted && (RunStatus is "started" or "queued");
}

/// <summary>
/// Creates an orchestrator-authored follow-up from the current durable delivery
/// failure. The operator failure panel and the gate-triage operator sweep
/// (AGT-3011) call this one service, so the two cannot disagree about which
/// failure is current, what the prompt says, or which lanes qualify.
/// </summary>
public sealed class TaskFailureContinuationService
{
    private readonly TaskScannerService _scanner;
    private readonly GitService _git;
    private readonly TaskIntegrationStatusService _integration;
    private readonly PipelineExecutionLog _pipeline;
    private readonly TaskRunnerService _runner;
    private readonly TimelineLog _timeline;

    public TaskFailureContinuationService(
        TaskScannerService scanner,
        GitService git,
        TaskIntegrationStatusService integration,
        PipelineExecutionLog pipeline,
        TaskRunnerService runner,
        TimelineLog timeline)
    {
        _scanner = scanner;
        _git = git;
        _integration = integration;
        _pipeline = pipeline;
        _runner = runner;
        _timeline = timeline;
    }

    /// <param name="source">Timeline <c>source</c> detail, e.g. <c>operator-failure-panel</c>.</param>
    /// <param name="automatic">Timeline <c>automatic</c> detail.</param>
    /// <param name="extraDetails">Additional timeline details the caller owns (sweep receipts).</param>
    public async Task<TaskFailureContinuationResult> ContinueAsync(
        TaskInfo job,
        string source,
        bool automatic,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? extraDetails = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.State is not (TaskStates.HumanReview or TaskStates.Escalated or TaskStates.AutoReview
                              or TaskStates.Completed or TaskStates.Archive))
            return Refused(409, "A failure continuation requires a delivered task.");

        var status = _integration.BuildLookup([job]).GetValueOrDefault(job.TaskKey);
        if (status?.ReachUnavailable == true)
            return Refused(409, "Git reach is unavailable. Re-check integration before continuing.");
        if (status?.Status == IntegrationStatuses.Integrated)
            return Refused(409, "The current delivery is already integrated.");
        var steps = _pipeline.Read(job.FolderPath)?.Steps;
        var subject = ReviewSubjectStore.Read(job.FolderPath);
        var failed = CurrentFailureStep(steps, status, subject);
        if (status?.Status == IntegrationStatuses.NoBranch && failed is null)
            return Refused(409, "This task has no delivery failure to continue.");
        if (status?.Failure is not null && failed is null)
            return Refused(409, "The recorded failure belongs to an earlier delivery. Re-check the task.");
        if (status is null && failed is null)
            return Refused(409, "Failure evidence is unavailable. Re-check the task.");
        if (status?.Failure is null && failed is null && status?.Status != IntegrationStatuses.Pending
            && status?.Status != IntegrationStatuses.Partial && status?.Status != IntegrationStatuses.MergedLocally)
            return Refused(409, "The current delivery has no failure to continue.");

        var stage = failed?.StepId ?? "delivery-pending";
        var currentFailure = status?.Failure is { } projectedFailure
            && failed?.StepId == projectedFailure.Stage ? projectedFailure : null;
        var reason = currentFailure?.Reason ?? failed?.Reason ?? status?.Detail
            ?? "The delivery is not reachable from the integration branch.";
        var findings = steps is null ? string.Empty : string.Join("; ", steps
            .Where(step => IsCurrentStep(step, subject) && step.Verdict is "concerns" or "block")
            .Select(step => $"{step.StepId}: {step.VerdictSummary ?? step.Reason}")
            .Take(8));
        var evidence = string.Join("\n", new[]
        {
            findings,
            failed?.VerdictSummary ?? string.Empty,
            ReadEvidenceExcerpt(job.FolderPath, failed?.EvidenceRef),
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var prompt = IntegrationContinuationPrompt.Build(
            job.Key ?? job.Id,
            subject?.ResultRef ?? status?.DeliveryRef,
            subject?.ResultSha,
            status?.IntegrationBranch ?? "develop",
            stage, reason,
            failed?.ConflictReport ?? currentFailure?.ConflictReport,
            evidence,
            integrationTip: ReadIntegrationTip(_git, job.WatchPath, status?.IntegrationBranch),
            evidenceRef: failed?.EvidenceRef);
        try
        {
            // No model, CLI, or thinking override: task pins remain authoritative.
            var response = await _runner.ContinueJobAsync(
                job.Id, prompt, job.WatchPath, mode: ContinueModes.Extend, ct: ct);
            var current = _scanner.FindJob(job.Id, job.WatchPath) ?? job;
            var details = new Dictionary<string, string>
            {
                ["automatic"] = automatic ? "true" : "false",
                ["source"] = source,
                ["failureStage"] = stage,
                ["failureCode"] = failed?.FailureCode ?? currentFailure?.Code ?? "delivery-pending",
                ["integrationBranch"] = status?.IntegrationBranch ?? "develop",
                ["deliveryRef"] = subject?.ResultRef ?? status?.DeliveryRef ?? string.Empty,
                ["failureEvidenceRef"] = failed?.EvidenceRef ?? string.Empty,
            };
            foreach (var (key, value) in extraDetails ?? new Dictionary<string, string>())
                details[key] = value;
            if (response.Status != "saved")
                _timeline.Append(current.FolderPath, TimelineEventKinds.IntegrationRecoveryQueued,
                    TimelineActors.System,
                    $"Continuation queued from {stage}: {reason}",
                    payloadRef: LatestExtension(current.FolderPath),
                    details: details);
            return new TaskFailureContinuationResult(202, null, response.Status, stage, job.Key,
                response.Queued?.Reason);
        }
        catch (TaskOperationException ex)
        {
            return Refused(ex.Status, ex.Message);
        }
    }

    internal static PipelineStepExecution? CurrentFailureStep(
        IEnumerable<PipelineStepExecution>? steps,
        TaskIntegrationStatus? status,
        ReviewSubjectRecord? subject)
    {
        if (status?.Status == IntegrationStatuses.Integrated
            || status?.ReachUnavailable == true)
            return null;
        var current = steps?.Where(step => IsCurrentStep(step, subject)).ToList();
        return current?.LastOrDefault(step => step.StepId == status?.Failure?.Stage
            && (step.Status == PipelineStepStatus.Failed || step.Verdict is "concerns" or "block"))
            ?? current?.LastOrDefault(step => step.Status == PipelineStepStatus.Failed
                || step.Verdict is "concerns" or "block");
    }

    private static TaskFailureContinuationResult Refused(int status, string error) => new(status, error);

    private static bool IsCurrentStep(PipelineStepExecution step, ReviewSubjectRecord? subject)
        => subject is null || subject.CompletedAtUtc == default
            || step.CompletedAt is { } completed
                && completed.ToUniversalTime() >= subject.CompletedAtUtc.UtcDateTime;

    private static string ReadEvidenceExcerpt(string folder, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return string.Empty;
        try
        {
            var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(folder, relativePath));
            if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path)) return string.Empty;
            using var stream = File.OpenRead(path);
            var bytes = new byte[Math.Min(4096, (int)Math.Min(stream.Length, int.MaxValue))];
            var count = stream.Read(bytes);
            return System.Text.Encoding.UTF8.GetString(bytes, 0, count);
        }
        catch (IOException) { return string.Empty; }
        catch (UnauthorizedAccessException) { return string.Empty; }
    }

    private static string LatestExtension(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "prompt-*.md", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Select(Path.GetFileName)
                .FirstOrDefault() ?? "prompt.md";
        }
        catch (IOException) { return "prompt.md"; }
        catch (UnauthorizedAccessException) { return "prompt.md"; }
    }

    private static string? ReadIntegrationTip(GitService git, string watchPath, string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch)) return null;
        try
        {
            var root = git.ResolveRepoRootForWatchPath(watchPath);
            if (string.IsNullOrWhiteSpace(root)) return null;
            var readRef = git.RemoteBranchExists(root, branch) ? "origin/" + branch : branch;
            return git.GetRefShaFresh(root, readRef);
        }
        catch (Exception) { return null; }
    }
}
