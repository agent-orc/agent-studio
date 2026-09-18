namespace AgentStudio.Pipeline;

public enum RemoteIntegrationContinuationAction
{
    None,
    StartAgentRound,
    LeaveForHumanReview,
}

/// <summary>
/// Pure continuation policy for an integration result. A result that cannot
/// retain unambiguous delivery SHA attribution receives a bounded number of
/// automatic steer rounds for that fenced delivery. Repeated ambiguity reaches
/// Human Review instead of opening an unbounded coding loop.
/// </summary>
public static class RemoteIntegrationContinuationPolicy
{
    public const int MaxAutomaticAgentRounds = 2;

    public static RemoteIntegrationContinuationAction Decide(
        MergeIntoIntegrationOutcome outcome,
        int automaticAgentRoundsUsed,
        int maximumAgentRounds = MaxAutomaticAgentRounds)
    {
        if (outcome != MergeIntoIntegrationOutcome.AgentRoundRequired)
            return RemoteIntegrationContinuationAction.None;

        return Math.Max(0, automaticAgentRoundsUsed) < Math.Max(1, maximumAgentRounds)
            ? RemoteIntegrationContinuationAction.StartAgentRound
            : RemoteIntegrationContinuationAction.LeaveForHumanReview;
    }
}

public sealed record IntegrationRecoveryBudgetUsage(
    string? DeliveryChainId,
    string? DeliverySha,
    int MatchingRounds,
    int LegacyRounds)
{
    public int Used => MatchingRounds + LegacyRounds;

    public string ExhaustedReason(int maximumRounds)
    {
        var limit = Math.Max(1, maximumRounds);
        var used = Math.Min(Used, limit);
        var delivery = string.IsNullOrWhiteSpace(DeliverySha)
            ? ""
            : $" for delivery {ShortSha(DeliverySha)}";
        var legacy = LegacyRounds == 0
            ? ""
            : $" (includes {LegacyRounds} legacy automatic recovery {RoundWord(LegacyRounds)} without a delivery identifier)";
        return $"automatic recovery budget used: {used}/{limit}{delivery}{legacy}";
    }

    private static string ShortSha(string sha)
        => sha.Length > 12 ? sha[..12] : sha;

    private static string RoundWord(int count)
        => count == 1 ? "round" : "rounds";
}

/// <summary>
/// Counts the shared integration-recovery budget for one immutable delivery.
/// The result ref and delivered SHA form the durable delivery-chain identity,
/// so another review of the same delivery shares its budget while a new fenced
/// delivery starts a new one. Identifier-less historical rows are counted
/// conservatively because they cannot be assigned safely to another delivery.
/// </summary>
public static class IntegrationRecoveryBudget
{
    public const string DeliveryChainIdKey = "deliveryChainId";

    public static string DeliveryChainId(ReviewSubjectRecord subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var deliveryRef = subject.ImmutableResultRef ?? subject.ResultRef ?? "missing-ref";
        return $"{deliveryRef.Trim()}@{subject.ResultSha.Trim().ToLowerInvariant()}";
    }

    public static IntegrationRecoveryBudgetUsage Count(
        IEnumerable<TimelineEvent> entries,
        ReviewSubjectRecord? subject)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var deliveryChainId = subject is null ? null : DeliveryChainId(subject);
        var matching = 0;
        var legacy = 0;
        foreach (var entry in entries)
        {
            if (entry.Kind != TimelineEventKinds.IntegrationRecoveryQueued)
                continue;

            var details = entry.Details;
            var automatic = details?.GetValueOrDefault("automatic") == "true"
                            || string.Equals(
                                details?.GetValueOrDefault("source"),
                                "acceptance-rail",
                                StringComparison.Ordinal);
            if (!automatic) continue;

            var recordedChainId = details?.GetValueOrDefault(DeliveryChainIdKey);
            if (string.IsNullOrWhiteSpace(recordedChainId))
            {
                legacy++;
                continue;
            }
            if (deliveryChainId is not null
                && string.Equals(recordedChainId, deliveryChainId, StringComparison.Ordinal))
            {
                matching++;
            }
        }

        return new IntegrationRecoveryBudgetUsage(
            deliveryChainId,
            subject?.ResultSha,
            matching,
            legacy);
    }
}

public sealed record IntegrationAgentRoundStartResult(
    bool Started,
    string Reason)
{
    public int BudgetUsed { get; init; }
    public int BudgetLimit { get; init; }
    public bool BudgetExhausted { get; init; }
}

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
    private readonly int _maximumRecoveryRounds;

    public IntegrationAgentRoundService(
        TaskScannerService scanner,
        TaskMutationService mutations,
        TaskStateMachine states,
        TimelineLog timeline,
        ILogger<IntegrationAgentRoundService> logger,
        IConfiguration? configuration = null)
    {
        _scanner = scanner;
        _mutations = mutations;
        _states = states;
        _timeline = timeline;
        _logger = logger;
        _maximumRecoveryRounds = configuration is null
            ? RemoteIntegrationContinuationPolicy.MaxAutomaticAgentRounds
            : AcceptanceRailOptions.FromConfiguration(configuration).MaxRequeues;
    }

    public Task<IntegrationAgentRoundStartResult> TryStartAsync(
        RemoteDeliveryIntegrationRequest request,
        MergeIntoIntegrationResult result)
    {
        var job = _scanner.FindJob(request.JobId, request.WatchPath);
        if (job is null)
            return Task.FromResult(Failed("The task disappeared before its automatic integration recovery round could start."));

        if (result.Outcome != MergeIntoIntegrationOutcome.AgentRoundRequired)
            return Task.FromResult(Failed("The integration result does not require an agent continuation."));

        var epoch = OperatorReviewRequeueService.ReadEpoch(job.FolderPath);
        var subject = ReviewSubjectStore.Read(job.FolderPath);
        if (subject is null || string.IsNullOrWhiteSpace(subject.ResultRef))
        {
            var reason = "The reviewed delivery has no fenced result ref for an automatic agent recovery round.";
            RecordFailure(job.FolderPath, request, result, reason, epoch);
            return Task.FromResult(Failed(reason));
        }

        var budget = IntegrationRecoveryBudget.Count(
            _timeline.ReadAll(job.FolderPath),
            subject);
        var action = RemoteIntegrationContinuationPolicy.Decide(
            result.Outcome,
            budget.Used,
            _maximumRecoveryRounds);
        if (action == RemoteIntegrationContinuationAction.LeaveForHumanReview)
        {
            var reason = budget.ExhaustedReason(_maximumRecoveryRounds);
            RecordFailure(job.FolderPath, request, result, reason, epoch, subject, budget);
            return Task.FromResult(Failed(
                reason,
                budgetUsed: Math.Min(budget.Used, _maximumRecoveryRounds),
                budgetLimit: _maximumRecoveryRounds,
                budgetExhausted: true));
        }

        if (!string.Equals(job.State, TaskStates.AutoReview, StringComparison.Ordinal))
        {
            var reason = $"Automatic integration recovery expected {TaskStates.AutoReview}, but the task is in {job.State}.";
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
                [IntegrationRecoveryBudget.DeliveryChainIdKey] = budget.DeliveryChainId!,
                ["deliveryRef"] = subject.ResultRef,
                ["resultSha"] = subject.ResultSha,
                ["integrationBranch"] = request.IntegrationBranch,
                ["mode"] = ContinueModes.Steer,
                ["reason"] = AttributionAmbiguousReason,
                ["supersededCommits"] = Invariant(supersession.MarkedCommits),
                ["budgetUsed"] = Invariant(budget.Used + 1),
                ["budgetLimit"] = Invariant(_maximumRecoveryRounds),
                ["legacyRecoveryRoundsCounted"] = Invariant(budget.LegacyRounds),
            });
        _logger.LogInformation(
            "integration-agent-round-started project={Project} job={JobId} deliveryChain={DeliveryChain} epoch={Epoch} position={Position} supersededCommits={SupersededCommits}",
            request.Project,
            request.JobId,
            budget.DeliveryChainId,
            epoch,
            position,
            supersession.MarkedCommits);
        return Task.FromResult(new IntegrationAgentRoundStartResult(true, prompt)
        {
            BudgetUsed = budget.Used + 1,
            BudgetLimit = _maximumRecoveryRounds,
        });
    }

    private void RecordFailure(
        string folderPath,
        RemoteDeliveryIntegrationRequest request,
        MergeIntoIntegrationResult result,
        string reason,
        int epoch,
        ReviewSubjectRecord? subject = null,
        IntegrationRecoveryBudgetUsage? budget = null)
    {
        var details = new Dictionary<string, string>
        {
            ["outcome"] = result.Outcome.ToString(),
            ["integrationBranch"] = request.IntegrationBranch,
            ["detail"] = result.Error ?? string.Empty,
            ["attemptEpoch"] = Invariant(epoch),
            ["stage"] = "pre-human-review",
        };
        if (subject is not null)
        {
            details[IntegrationRecoveryBudget.DeliveryChainIdKey] =
                budget?.DeliveryChainId ?? IntegrationRecoveryBudget.DeliveryChainId(subject);
            details["deliveryRef"] = subject.ResultRef ?? string.Empty;
            details["resultSha"] = subject.ResultSha;
        }
        if (budget is not null)
            details["legacyRecoveryRoundsCounted"] = Invariant(budget.LegacyRounds);

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
        var conflictedFiles = result.ConflictReport?.ConflictedFiles.Count > 0
            ? string.Join(", ", result.ConflictReport.ConflictedFiles)
            : result.ConflictedFiles.Count > 0
                ? string.Join(", ", result.ConflictedFiles)
                : "none recorded";
        return $"Automatic integration recovery for {job.Key ?? job.Id}. "
            + $"The platform first tried a direct merge of delivery '{subject.ResultRef}' at {subject.ResultSha} into '{request.IntegrationBranch}', then a mechanical three-way/rerere merge, and only then a mechanical rebase. "
            + "Produce a delivery state that integrates cleanly. Prefer merging the latest integration branch into the existing delivery branch and resolving conflicts there over rewriting delivery history. "
            + $"Conflicted files from the integration report: {conflictedFiles}. "
            + "If rewriting is unavoidable, retain a one-to-one delivery commit mapping: do not squash, split, drop, or combine delivery commits. "
            + "Do not redo the feature work. Run the relevant tests and finish with the normal task terminal sentinel. "
            + "Do not move or push the integration branch ref; publish only the updated delivery branch for a new delivery gate and review round.";
    }

    private static IntegrationAgentRoundStartResult Failed(
        string reason,
        int budgetUsed = 0,
        int budgetLimit = 0,
        bool budgetExhausted = false)
        => new(false, reason)
        {
            BudgetUsed = budgetUsed,
            BudgetLimit = budgetLimit,
            BudgetExhausted = budgetExhausted,
        };

    private static string Invariant(int value)
        => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
