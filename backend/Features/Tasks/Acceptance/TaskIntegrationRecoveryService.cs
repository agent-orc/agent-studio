namespace AgentStudio.Tasks;

public sealed record TaskIntegrationRecoveryResult(
    bool Queued,
    string? Error = null,
    bool InternalError = false,
    int Position = 0,
    string? DeliveryRef = null,
    string? ResultSha = null,
    string? IntegrationBranch = null,
    int? RetryNumber = null);

/// <summary>
/// Shared application boundary for operator-triggered and acceptance-rail
/// rebase recovery. The service persists the steer, supersedes the failed
/// delivery generation, and promotes the card through the existing state
/// machine. It never edits Git history.
/// </summary>
public sealed class TaskIntegrationRecoveryService
{
    public const string AcceptanceRailSource = "acceptance-rail";
    public const string OperatorSource = "operator";

    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly TaskStateMachine _states;
    private readonly TimelineLog _timeline;
    private readonly ILogger<TaskIntegrationRecoveryService> _logger;

    public TaskIntegrationRecoveryService(
        TaskScannerService scanner,
        TaskMutationService mutations,
        TaskStateMachine states,
        TimelineLog timeline,
        ILogger<TaskIntegrationRecoveryService> logger)
    {
        _scanner = scanner;
        _mutations = mutations;
        _states = states;
        _timeline = timeline;
        _logger = logger;
    }

    public TaskIntegrationRecoveryResult Queue(
        TaskInfo job,
        TaskIntegrationStatus status,
        string failureCode,
        string source,
        int? retryNumber = null)
    {
        var subject = ReviewSubjectStore.Read(job.FolderPath);
        if (subject is null || string.IsNullOrWhiteSpace(subject.ResultRef))
        {
            return Failed("The accepted task has no fenced remote delivery ref to recover.");
        }

        var integrationBranch = status.IntegrationBranch;
        var prompt = BuildPrompt(job, subject, integrationBranch, status.Failure?.ConflictReport);
        var savedReason = source == AcceptanceRailSource && retryNumber is not null
            ? $"{AcceptanceRailSource}:{failureCode}:retry-{retryNumber.Value}"
            : failureCode;
        var alreadyPrepared = string.Equals(
                job.PendingIntent?.SavedReason,
                savedReason,
                StringComparison.Ordinal)
            && string.Equals(job.PendingIntent?.Prompt, prompt, StringComparison.Ordinal);

        if (!alreadyPrepared)
        {
            var intent = _mutations.SavePendingIntent(
                job.Id,
                ContinueModes.Steer,
                prompt,
                reason: savedReason,
                activeJobId: null,
                watchPath: job.WatchPath);
            if (intent is null)
                return Failed("The integration recovery steer intent could not be persisted.");

            if (!_mutations.AppendContinuationNote(job.Id, prompt, job.WatchPath))
                return Failed("The integration recovery steer could not be appended to the task prompt.");
        }

        var current = _scanner.FindJob(job.Id, job.WatchPath);
        if (current is null)
            return Failed("The task disappeared before integration recovery could be queued.");

        var supersession = _mutations.SupersedeCurrentDeliveryOnFolder(
            current.FolderPath,
            TaskCommitSupersession.PendingAttempt);
        if (!supersession.Succeeded)
        {
            return Failed(
                "The recovery intent was persisted, but the superseded delivery history could not be marked.",
                internalError: true);
        }

        var position = _states.PromoteToReadyTop(
            current.Id,
            current.WatchPath,
            cause: TimelineActors.System,
            transitionCause: LaneChangeCauses.IntegrationRecovery,
            transitionDetail: retryNumber is null
                ? failureCode
                : $"{failureCode}:retry-{retryNumber.Value}",
            expectedSourceState: job.State);
        var queued = _scanner.FindJob(current.Id, current.WatchPath);
        if (position <= 0 || queued is null || queued.State != TaskStates.Ready)
        {
            return Failed(
                "The recovery prompt was persisted, but the task could not be queued in Ready.");
        }

        var details = new Dictionary<string, string>
        {
            ["automatic"] = (source == AcceptanceRailSource).ToString().ToLowerInvariant(),
            ["source"] = source,
            ["deliveryRef"] = subject.ResultRef,
            ["resultSha"] = subject.ResultSha,
            ["integrationBranch"] = integrationBranch,
            ["mode"] = ContinueModes.Steer,
            ["reason"] = failureCode,
            ["supersededCommits"] = Invariant(supersession.MarkedCommits),
        };
        if (retryNumber is not null)
            details["retryNumber"] = Invariant(retryNumber.Value);
        if (source == AcceptanceRailSource)
            details["attemptEpoch"] = Invariant(
                OperatorReviewRequeueService.ReadEpoch(queued.FolderPath));

        _timeline.Append(
            queued.FolderPath,
            TimelineEventKinds.IntegrationRecoveryQueued,
            TimelineActors.System,
            $"Integration recovery queued: reconcile {subject.ResultRef} with {integrationBranch}.",
            payloadRef: "prompt.md",
            details: details);
        _logger.LogInformation(
            "integration-recovery-queued source={Source} project={Project} job={JobId} retry={RetryNumber} position={Position}",
            source,
            queued.ProjectName,
            queued.Id,
            retryNumber,
            position);

        return new TaskIntegrationRecoveryResult(
            true,
            Position: position,
            DeliveryRef: subject.ResultRef,
            ResultSha: subject.ResultSha,
            IntegrationBranch: integrationBranch,
            RetryNumber: retryNumber);
    }

    internal static string BuildPrompt(
        TaskInfo job,
        ReviewSubjectRecord subject,
        string integrationBranch,
        IntegrationConflictReport? conflictReport = null)
    {
        var conflictedFiles = conflictReport?.ConflictedFiles.Count > 0
            ? string.Join(", ", conflictReport.ConflictedFiles)
            : "none recorded";
        return "## STEER\n\n"
            + $"Integration recovery for {job.Key ?? job.Id}. "
            + $"Resume the existing delivery branch '{subject.ResultRef}' at the fenced result {subject.ResultSha}. "
            + $"Fetch the latest 'origin/{integrationBranch}' and produce a delivery state that integrates cleanly. "
            + $"Prefer merging 'origin/{integrationBranch}' into the existing delivery branch and resolving conflicts there over rewriting delivery history. "
            + $"Conflicted files from the integration report: {conflictedFiles}. "
            + "If rewriting is unavoidable, retain a one-to-one delivery commit mapping: do not squash, split, drop, or combine delivery commits. "
            + "Do not redo the feature work. Run the relevant tests and finish with the normal task terminal sentinel. "
            + "Do not move or push the integration branch ref; publish only the updated delivery branch for a new delivery gate and review round.";
    }

    private static TaskIntegrationRecoveryResult Failed(
        string error,
        bool internalError = false)
        => new(false, error, internalError);

    private static string Invariant(int value)
        => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
