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
    public const int MaxAutomaticAgentRounds = 1;

    public static RemoteIntegrationContinuationAction Decide(
        MergeIntoIntegrationOutcome outcome,
        int automaticAgentRoundsUsed,
        int maximumAgentRounds = MaxAutomaticAgentRounds)
    {
        if (outcome is not (MergeIntoIntegrationOutcome.AgentRoundRequired or MergeIntoIntegrationOutcome.Conflict))
            return RemoteIntegrationContinuationAction.None;

        return Math.Max(0, automaticAgentRoundsUsed) < Math.Min(MaxAutomaticAgentRounds,
            Math.Max(1, maximumAgentRounds))
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
    /// <summary>The integration target the rounds were counted for, when known.</summary>
    public string? IntegrationBranch { get; init; }

    public int Used => MatchingRounds + LegacyRounds;

    public string ExhaustedReason(int maximumRounds)
    {
        var limit = Math.Max(1, maximumRounds);
        var used = Math.Min(Used, limit);
        var target = string.IsNullOrWhiteSpace(IntegrationBranch)
            ? " for this card"
            : $" for this card on {AgentStudio.Tasks.TaskIntegrationBranch.Name(IntegrationBranch)}";
        var legacy = LegacyRounds == 0
            ? ""
            : $" (includes {LegacyRounds} legacy automatic recovery {RoundWord(LegacyRounds)} without a delivery identifier)";
        return $"automatic recovery budget used: {used}/{limit}{target}{legacy}";
    }

    private static string RoundWord(int count)
        => count == 1 ? "round" : "rounds";
}

/// <summary>
/// Counts the shared integration-recovery budget of one card for one
/// integration target. AGT-3968: the budget no longer restarts with every
/// delivery. Each automatic round publishes a new delivery SHA, so a budget
/// keyed by the delivery chain (result ref and SHA) let AGT-3897 take eight
/// rounds under a limit of one. Every automatic round of the card counts,
/// whichever delivery it started from; a round recorded for another
/// integration branch does not. Rows without a delivery identifier still
/// count and stay named in the park reason.
/// </summary>
public static class IntegrationRecoveryBudget
{
    public const string DeliveryChainIdKey = "deliveryChainId";
    public const string IntegrationBranchKey = "integrationBranch";

    public static string DeliveryChainId(ReviewSubjectRecord subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var deliveryRef = subject.ImmutableResultRef ?? subject.ResultRef ?? "missing-ref";
        return $"{deliveryRef.Trim()}@{subject.ResultSha.Trim().ToLowerInvariant()}";
    }

    public static IntegrationRecoveryBudgetUsage Count(
        IEnumerable<TimelineEvent> entries,
        ReviewSubjectRecord? subject,
        string? integrationBranch = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var deliveryChainId = subject is null ? null : DeliveryChainId(subject);
        var target = string.IsNullOrWhiteSpace(integrationBranch)
            ? null
            : AgentStudio.Tasks.TaskIntegrationBranch.Name(integrationBranch);
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

            var recordedBranch = details?.GetValueOrDefault(IntegrationBranchKey);
            if (target is not null && !string.IsNullOrWhiteSpace(recordedBranch)
                && !string.Equals(AgentStudio.Tasks.TaskIntegrationBranch.Name(recordedBranch), target,
                    StringComparison.Ordinal))
                continue;

            if (string.IsNullOrWhiteSpace(details?.GetValueOrDefault(DeliveryChainIdKey)))
                legacy++;
            else
                matching++;
        }

        return new IntegrationRecoveryBudgetUsage(
            deliveryChainId,
            subject?.ResultSha,
            matching,
            legacy)
        {
            IntegrationBranch = target,
        };
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
    private readonly ProjectSettingsService? _settings;
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
        IConfiguration? configuration = null,
        ProjectSettingsService? settings = null)
    {
        _scanner = scanner;
        _settings = settings;
        _mutations = mutations;
        _states = states;
        _timeline = timeline;
        _logger = logger;
        _maximumRecoveryRounds = configuration is null
            ? RemoteIntegrationContinuationPolicy.MaxAutomaticAgentRounds
            : Math.Min(RemoteIntegrationContinuationPolicy.MaxAutomaticAgentRounds,
                AcceptanceRailOptions.FromConfiguration(configuration).MaxRequeues);
    }

    public Task<IntegrationAgentRoundStartResult> TryStartAsync(
        RemoteDeliveryIntegrationRequest request,
        MergeIntoIntegrationResult result)
        => TryStartAsync(request, result, involvedCards: []);

    /// <summary>
    /// AGT-3968: one sentence that names the conflicting files and the other
    /// cards whose integrated commits touched them, for a park reason.
    /// </summary>
    public static string ConflictSummary(IReadOnlyList<string> conflictedFiles, IReadOnlyList<string> involvedCards)
        => (conflictedFiles.Count == 0 ? "" : $" Conflicting files: {string.Join(", ", conflictedFiles)}.")
           + (involvedCards.Count == 0 ? "" : $" Other cards involved: {string.Join(", ", involvedCards)}.");

    public Task<IntegrationAgentRoundStartResult> TryStartAsync(
        RemoteDeliveryIntegrationRequest request,
        MergeIntoIntegrationResult result,
        IReadOnlyList<string> involvedCards)
    {
        var conflictedFiles = result.ConflictReport?.ConflictedFiles is { Count: > 0 } reported
            ? reported : result.ConflictedFiles;
        var job = _scanner.FindJob(request.JobId, request.WatchPath);
        if (job is null)
            return Task.FromResult(Failed("The task disappeared before its automatic integration recovery round could start."));

        if (result.Outcome is not (MergeIntoIntegrationOutcome.AgentRoundRequired or MergeIntoIntegrationOutcome.Conflict))
            return Task.FromResult(Failed("The integration result does not require an agent continuation."));

        if (_settings?.Get(job.ProjectName).AutomaticFailureContinuationsEnabled == false)
        {
            const string reason = "Automatic failure continuations are disabled for this project.";
            RecordFailure(job.FolderPath, request, result, reason,
                OperatorReviewRequeueService.ReadEpoch(job.FolderPath));
            return Task.FromResult(Failed(reason));
        }

        var epoch = OperatorReviewRequeueService.ReadEpoch(job.FolderPath);
        var subject = ReviewSubjectStore.Read(job.FolderPath);
        if (subject is null || string.IsNullOrWhiteSpace(
                subject.ImmutableResultRef ?? subject.ResultRef))
        {
            var reason = "The reviewed delivery has no fenced result ref for an automatic agent recovery round.";
            RecordFailure(job.FolderPath, request, result, reason, epoch);
            return Task.FromResult(Failed(reason));
        }

        var budget = IntegrationRecoveryBudget.Count(
            _timeline.ReadAll(job.FolderPath),
            subject,
            request.IntegrationBranch);
        var action = RemoteIntegrationContinuationPolicy.Decide(
            result.Outcome,
            budget.Used,
            _maximumRecoveryRounds);
        if (action == RemoteIntegrationContinuationAction.LeaveForHumanReview)
        {
            var reason = budget.ExhaustedReason(_maximumRecoveryRounds)
                + "." + ConflictSummary(conflictedFiles, involvedCards);
            RecordFailure(job.FolderPath, request, result, reason, epoch, subject, budget,
                conflictedFiles, involvedCards);
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

        var prompt = BuildPrompt(job, subject, request, result, involvedCards);
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

        // Ready is immediately claimable. Persist both the route and typed
        // lineage first, just like acceptance-rail integration recovery.
        var route = TaskIntegrationRecoveryService.SelectRecoveryRoute(job, new ModelRoutingPolicyRegistry());
        if (route.Selected != route.Previous)
        {
            var level = route.Selected[(route.Selected.LastIndexOf('/') + 1)..];
            if (!_mutations.SetRecoveryThinkingLevel(job.Id, level, job.WatchPath))
                return Task.FromResult(Failed("The integration recovery reasoning route could not be persisted."));
        }
        SessionContinuationLedgerStore.SaveDelta(job.FolderPath, new AgentStudio.TaskServer.Contracts.MechanicalRoundDelta(
            result.ConflictReport?.IntegrationTipSha ?? result.PreviousIntegrationSha ?? string.Empty,
            subject.ImmutableResultRef ?? subject.ResultRef!,
            subject.ResultSha,
            result.ConflictReport?.ConflictedFiles ?? result.ConflictedFiles,
            prompt,
            IntegrationContinuationPrompt.VerificationScope.Trim()));

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
                ["deliveryRef"] = subject.ImmutableResultRef ?? subject.ResultRef!,
                ["resultSha"] = subject.ResultSha,
                ["integrationBranch"] = request.IntegrationBranch,
                ["mode"] = ContinueModes.Steer,
                ["reason"] = AttributionAmbiguousReason,
                ["supersededCommits"] = Invariant(supersession.MarkedCommits),
                ["conflictedFiles"] = string.Join(", ", conflictedFiles),
                ["involvedCards"] = string.Join(", ", involvedCards),
                ["budgetUsed"] = Invariant(budget.Used + 1),
                ["budgetLimit"] = Invariant(_maximumRecoveryRounds),
                ["legacyRecoveryRoundsCounted"] = Invariant(budget.LegacyRounds),
                ["previousRoute"] = route.Previous,
                ["selectedRoute"] = route.Selected,
                ["routeReason"] = route.Reason,
                ["policyVersion"] = route.PolicyVersion,
            });
        _logger.LogInformation(
            "integration-agent-round-started project={Project} job={JobId} deliveryChain={DeliveryChain} epoch={Epoch} position={Position} supersededCommits={SupersededCommits}",
            request.Project,
            request.JobId,
            budget.DeliveryChainId,
            epoch,
            position,
            supersession.MarkedCommits);
        IntegrationConflictLedger.Default.Resolve(request.Project, subject.TaskKey, subject.ResultSha,
            request.IntegrationBranch, "agent-round");
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
        IntegrationRecoveryBudgetUsage? budget = null,
        IReadOnlyList<string>? conflictedFiles = null,
        IReadOnlyList<string>? involvedCards = null)
    {
        var conflictSubject = subject ?? ReviewSubjectStore.Read(folderPath);
        if (conflictSubject is not null)
            IntegrationConflictLedger.Default.Resolve(request.Project, conflictSubject.TaskKey,
                conflictSubject.ResultSha, request.IntegrationBranch, "parked");
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
            details["deliveryRef"] = subject.ImmutableResultRef ?? subject.ResultRef ?? string.Empty;
            details["resultSha"] = subject.ResultSha;
        }
        if (budget is not null)
            details["legacyRecoveryRoundsCounted"] = Invariant(budget.LegacyRounds);
        if (conflictedFiles is { Count: > 0 })
            details["conflictedFiles"] = string.Join(", ", conflictedFiles);
        if (involvedCards is { Count: > 0 })
            details["involvedCards"] = string.Join(", ", involvedCards);

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
        MergeIntoIntegrationResult result,
        IReadOnlyList<string> involvedCards)
    {
        return IntegrationContinuationPrompt.Build(
            job.Key ?? job.Id, subject.ImmutableResultRef ?? subject.ResultRef, subject.ResultSha,
            request.IntegrationBranch, "merge-into-develop",
            result.Error ?? "Delivery attribution needs a new agent round.", result.ConflictReport,
            conflictedFiles: result.ConflictedFiles, runnerMergesIntegration: true,
            involvedCards: involvedCards);
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
