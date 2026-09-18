namespace AgentStudio.Pipeline;

public enum RemoteIntegrationContinuationAction
{
    None,
    StartAgentRound,
    LeaveForHumanReview,
}

/// <summary>
/// Pure continuation policy for an integration result. A result that cannot
/// retain unambiguous delivery SHA attribution receives a bounded automatic
/// recovery budget for the current delivery chain. Repeated ambiguity reaches
/// Human Review instead of opening an unbounded coding loop.
/// </summary>
public static class RemoteIntegrationContinuationPolicy
{
    public const int MaxAutomaticAgentRounds = 2;

    public static RemoteIntegrationContinuationAction Decide(
        MergeIntoIntegrationOutcome outcome,
        int automaticAgentRoundsUsed)
    {
        if (outcome != MergeIntoIntegrationOutcome.AgentRoundRequired)
            return RemoteIntegrationContinuationAction.None;

        return Math.Max(0, automaticAgentRoundsUsed) < MaxAutomaticAgentRounds
            ? RemoteIntegrationContinuationAction.StartAgentRound
            : RemoteIntegrationContinuationAction.LeaveForHumanReview;
    }
}

public sealed record IntegrationAgentRoundStartResult(
    bool Started,
    string Reason,
    bool BudgetExhausted = false,
    int AutomaticRoundsUsed = 0,
    int AutomaticRoundsLimit = RemoteIntegrationContinuationPolicy.MaxAutomaticAgentRounds);

/// <summary>
/// Applies the bounded side effects for an automatic integration-recovery
/// round: persist a steer intent, retain the superseded delivery as history,
/// queue the original card at the front of Ready, and emit the replacement
/// timeline statement. The service never authors Git history itself.
/// </summary>
public sealed class IntegrationAgentRoundService
{
    public const string AttributionAmbiguousReason = "delivery-attribution-ambiguous";

    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly TaskStateMachine _states;
    private readonly TimelineLog _timeline;
    private readonly ILogger<IntegrationAgentRoundService> _logger;

    public IntegrationAgentRoundService(
        TaskScannerService scanner,
        TaskMutationService mutations,
        TaskStateMachine states,
        TimelineLog timeline,
        ILogger<IntegrationAgentRoundService> logger)
    {
        _scanner = scanner;
        _mutations = mutations;
        _states = states;
        _timeline = timeline;
        _logger = logger;
    }

    public Task<IntegrationAgentRoundStartResult> TryStartAsync(
        RemoteDeliveryIntegrationRequest request,
        MergeIntoIntegrationResult result)
    {
        var job = _scanner.FindJob(request.JobId, request.WatchPath);
        if (job is null)
            return Task.FromResult(Failed("The task disappeared before its automatic integration recovery round could start."));

        var epoch = OperatorReviewRequeueService.ReadEpoch(job.FolderPath);
        var automaticRoundsUsed = _timeline.ReadAll(job.FolderPath).Count(entry =>
            entry.Kind == TimelineEventKinds.IntegrationRecoveryQueued
            && entry.Details?.GetValueOrDefault("automatic") == "true"
            && entry.Details?.GetValueOrDefault("attemptEpoch") == Invariant(epoch));
        var action = RemoteIntegrationContinuationPolicy.Decide(
            result.Outcome,
            automaticRoundsUsed);
        if (action == RemoteIntegrationContinuationAction.None)
            return Task.FromResult(Failed("The integration result does not require an agent continuation."));
        if (action == RemoteIntegrationContinuationAction.LeaveForHumanReview)
        {
            var reason = $"automatic recovery budget used: {automaticRoundsUsed}/{RemoteIntegrationContinuationPolicy.MaxAutomaticAgentRounds}";
            RecordFailure(
                job.FolderPath,
                request,
                result,
                reason,
                epoch,
                automaticRoundsUsed);
            return Task.FromResult(new IntegrationAgentRoundStartResult(
                false,
                reason,
                BudgetExhausted: true,
                AutomaticRoundsUsed: automaticRoundsUsed));
        }

        if (!string.Equals(job.State, TaskStates.AutoReview, StringComparison.Ordinal))
        {
            var reason = $"Automatic integration recovery expected {TaskStates.AutoReview}, but the task is in {job.State}.";
            RecordFailure(job.FolderPath, request, result, reason, epoch);
            return Task.FromResult(Failed(reason));
        }

        var subject = ReviewSubjectStore.Read(job.FolderPath);
        if (subject is null || string.IsNullOrWhiteSpace(subject.ResultRef))
        {
            var reason = "The reviewed delivery has no fenced result ref for an automatic agent recovery round.";
            RecordFailure(job.FolderPath, request, result, reason, epoch);
            return Task.FromResult(Failed(reason));
        }

        var prompt = BuildPrompt(job, subject, request, result);
        var intent = _mutations.SavePendingIntent(
            job.Id,
            ContinueModes.Steer,
            prompt,
            reason: AttributionAmbiguousReason,
            activeJobId: null,
            watchPath: job.WatchPath);
        if (intent is null)
        {
            var reason = "The automatic integration recovery steer intent could not be persisted.";
            RecordFailure(job.FolderPath, request, result, reason, epoch);
            return Task.FromResult(Failed(reason));
        }

        _mutations.AppendContinuationNote(job.Id, prompt, job.WatchPath);
        var supersession = _mutations.SupersedeCurrentDeliveryOnFolder(
            job.FolderPath,
            TaskCommitSupersession.PendingAttempt);
        if (!supersession.Succeeded)
        {
            var reason = "The recovery steer was saved, but the ambiguous delivery could not be retained as superseded history.";
            RecordFailure(job.FolderPath, request, result, reason, epoch);
            return Task.FromResult(Failed(reason));
        }

        var position = _states.PromoteToReadyTop(
            job.Id, job.WatchPath,
            transitionCause: LaneChangeCauses.IntegrationRecovery,
            transitionDetail: result.Outcome.ToString());
        var queued = _scanner.FindJob(job.Id, job.WatchPath);
        if (position <= 0 || queued is null || queued.State != TaskStates.Ready)
        {
            var reason = "The recovery steer was saved, but the task could not be queued for its automatic agent round.";
            RecordFailure(queued?.FolderPath ?? job.FolderPath, request, result, reason, epoch);
            return Task.FromResult(Failed(reason));
        }

        _timeline.Append(
            queued.FolderPath,
            TimelineEventKinds.IntegrationRecoveryQueued,
            TimelineActors.System,
            "Automatically started a new agent round to preserve unambiguous delivery SHA attribution.",
            payloadRef: "orchestrator-follow-up.md",
            details: new Dictionary<string, string>
            {
                ["automatic"] = "true",
                ["attemptEpoch"] = Invariant(epoch),
                ["deliveryRef"] = subject.ResultRef,
                ["resultSha"] = subject.ResultSha,
                ["integrationBranch"] = request.IntegrationBranch,
                ["mode"] = ContinueModes.Steer,
                ["reason"] = AttributionAmbiguousReason,
                ["supersededCommits"] = Invariant(supersession.MarkedCommits),
            });
        _logger.LogInformation(
            "integration-agent-round-started project={Project} job={JobId} epoch={Epoch} position={Position} supersededCommits={SupersededCommits}",
            request.Project,
            request.JobId,
            epoch,
            position,
            supersession.MarkedCommits);
        return Task.FromResult(new IntegrationAgentRoundStartResult(
            true,
            prompt,
            AutomaticRoundsUsed: automaticRoundsUsed + 1));
    }

    private void RecordFailure(
        string folderPath,
        RemoteDeliveryIntegrationRequest request,
        MergeIntoIntegrationResult result,
        string reason,
        int epoch,
        int? automaticRoundsUsed = null)
    {
        var details = new Dictionary<string, string>
        {
            ["outcome"] = result.Outcome.ToString(),
            ["integrationBranch"] = request.IntegrationBranch,
            ["detail"] = result.Error ?? string.Empty,
            ["attemptEpoch"] = Invariant(epoch),
            ["stage"] = "pre-human-review",
        };
        if (automaticRoundsUsed.HasValue)
        {
            details["automaticRecoveryRoundsUsed"] = Invariant(automaticRoundsUsed.Value);
            details["automaticRecoveryRoundsLimit"] = Invariant(
                RemoteIntegrationContinuationPolicy.MaxAutomaticAgentRounds);
        }
        _timeline.Append(
            folderPath,
            TimelineEventKinds.IntegrationFailed,
            TimelineActors.System,
            reason,
            details: details);
    }

    private static string BuildPrompt(
        TaskInfo job,
        ReviewSubjectRecord subject,
        RemoteDeliveryIntegrationRequest request,
        MergeIntoIntegrationResult result)
    {
        var report = result.ConflictReport;
        var conflicts = report?.ConflictedFiles.Count > 0
            ? string.Join(", ", report.ConflictedFiles)
            : result.ConflictedFiles.Count > 0
                ? string.Join(", ", result.ConflictedFiles.Take(IntegrationConflictReport.MaxConflictedFiles))
                : "none recorded";
        var count = report?.ConflictedFileCount ?? result.ConflictedFiles.Count;
        var fallbackOutcome = report is null
            ? "Rebase fallback outcome: delivery commit cardinality was not preserved. "
            : string.Empty;
        return $"Automatic integration recovery for {job.Key ?? job.Id}. "
               + $"The platform first tried a direct merge of delivery '{subject.ResultRef}' at {subject.ResultSha} into '{request.IntegrationBranch}', then a mechanical three-way/rerere merge, and only then a mechanical rebase fallback. "
               + $"All three stages failed. Conflicted files ({count}): {conflicts}. "
               + fallbackOutcome
               + $"Produce an updated delivery that integrates cleanly. Prefer merging '{request.IntegrationBranch}' into the card branch and resolving the conflicts over rebasing or otherwise rewriting history. "
               + "Preserve a one-to-one delivery commit history: do not squash, split, drop, or combine existing delivery commits. "
               + "Run the relevant tests and finish with the normal task terminal sentinel. Do not merge or push the integration branch itself; publish only the updated delivery branch for a new delivery gate and review round.";
    }

    private static IntegrationAgentRoundStartResult Failed(string reason)
        => new(false, reason);

    private static string Invariant(int value)
        => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
