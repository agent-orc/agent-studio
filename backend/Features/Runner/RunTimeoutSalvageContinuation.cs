namespace AgentStudio.Runner;

/// <summary>
/// How the previous round ended, in the words the continuation prompt and the
/// ledger use. One record keeps the AGT-2861 timeout wording and the AGT-2870
/// worker-loss wording on the same builder instead of forking the prompt.
/// </summary>
/// <param name="Reason">
/// Machine reason on the saved intent, the lane transition detail, and the
/// timeline entry.
/// </param>
/// <param name="LaneCause">Prefix of the move cause, followed by the round counter.</param>
/// <param name="Ending">What the previous round did, as a verb phrase.</param>
/// <param name="RepeatNoun">What a second occurrence is called in the budget sentence.</param>
/// <param name="Evidence">
/// One optional sentence of diagnostic context (the lost worker's last words);
/// null when the previous round left none.
/// </param>
/// <param name="StartsFromSalvage">
/// Whether the next round's worktree is prepared on the salvage commit instead
/// of the integration branch. A salvage continuation always carries its work
/// into the checkout before the next agent starts.
/// </param>
public sealed record RunContinuationCause(
    string Reason,
    string LaneCause,
    string Ending,
    string RepeatNoun,
    string? Evidence = null,
    bool StartsFromSalvage = false)
{
    /// <summary>AGT-2861: the run hit its timeout with a salvaged worktree.</summary>
    public static RunContinuationCause Timeout(string? reportedReason)
        => new(
            RunTimeoutSalvageContinuationPolicy.ContinuationReason,
            "remote-run-timeout-continuation",
            $"hit the run timeout{ReportedOutcome(reportedReason)}",
            "timeout",
            StartsFromSalvage: true);

    /// <summary>AGT-2870: the detached worker died before it recorded a result.</summary>
    public static RunContinuationCause WorkerLost(string? reportedReason, string? crashLine)
        => new(
            LostWorkerContinuationPolicy.ContinuationReason,
            "remote-worker-lost-continuation",
            $"lost its worker process before it recorded a result{ReportedOutcome(reportedReason)}",
            "loss",
            ComposeEvidence(crashLine),
            StartsFromSalvage: true);

    public string Describe() => Ending;

    private static string ReportedOutcome(string? reportedReason)
    {
        var reason = (reportedReason ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return reason.Length == 0 ? string.Empty : $" (reported outcome: {reason})";
    }

    private static string? ComposeEvidence(string? crashLine)
    {
        var line = (crashLine ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (line.Length == 0) return null;
        if (line.Length > 300) line = line[..300];
        return $"The worker's last diagnostic line was: \"{line}\".";
    }
}

/// <summary>
/// Outcome of one attempt to open an automatic continuation round.
/// <paramref name="Reason"/> carries the rendered continuation prompt when the
/// round started and the refusal text when it did not.
/// </summary>
public sealed record RunTimeoutContinuationResult(
    bool Started,
    string Reason,
    int Round = 0,
    int AttemptEpoch = 0);

/// <summary>
/// Applies the bounded side effects for one automatic continuation round on a
/// salvage commit: persist the continuation intent, append the finishing
/// instruction to <c>prompt.md</c> (the text the remote runner fetches
/// verbatim), pin the round to a clean CLI context, return the card to the
/// front of Ready under the completing attempt's authority, and state the round
/// on the timeline. The service never authors Git history.
/// </summary>
public sealed class RunTimeoutContinuationService
{
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly TaskStateMachine _states;
    private readonly TaskTransitionService _transitions;
    private readonly TimelineLog _timeline;
    private readonly ILogger<RunTimeoutContinuationService> _logger;
    private readonly IConfiguration _configuration;

    public RunTimeoutContinuationService(
        TaskScannerService scanner,
        TaskMutationService mutations,
        TaskStateMachine states,
        TaskTransitionService transitions,
        TimelineLog timeline,
        ILogger<RunTimeoutContinuationService> logger,
        IConfiguration configuration)
    {
        _scanner = scanner;
        _mutations = mutations;
        _states = states;
        _transitions = transitions;
        _timeline = timeline;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Automatic continuation rounds already spent in the card's current
    /// delivery generation. An explicit operator requeue opens a new
    /// review-attempt epoch and therefore a new bounded opportunity; automatic
    /// moves never increment the epoch.
    /// </summary>
    public int CountAutomaticRounds(TaskInfo task)
    {
        var epoch = OperatorReviewRequeueService.ReadEpoch(task.FolderPath);
        return _timeline.ReadAll(task.FolderPath).Count(entry =>
            entry.Kind == TimelineEventKinds.ContinuationRoundStarted
            && entry.Details?.GetValueOrDefault("automatic") == "true"
            && entry.Details?.GetValueOrDefault("attemptEpoch") == Invariant(epoch));
    }

    public async Task<RunTimeoutContinuationResult> StartAsync(
        TaskInfo task,
        RunSalvageReference salvage,
        string reportedReason,
        string attemptId,
        AttemptWriteReference authorityWrite,
        CancellationToken ct,
        RunContinuationCause? cause = null)
    {
        var workspace = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
            return Failed("The card round budget could not be read.");
        var cardBudget = CardRoundBudget.Evaluate(
            ReviewDecisionLog.ReadAll(workspace, task.ProjectName),
            _timeline.ReadAll(task.FolderPath), task.Id,
            CardRoundBudget.ResolveAllowed(_configuration));
        if (cardBudget.Exhausted)
            return Failed($"Automatic round budget spent ({cardBudget.Used} of {cardBudget.Allowed}).");
        var epoch = OperatorReviewRequeueService.ReadEpoch(task.FolderPath);
        var round = CountAutomaticRounds(task) + 1;
        cause ??= RunContinuationCause.Timeout(reportedReason);
        var prompt = BuildPrompt(task, salvage, cause);
        var promptPath = Path.Combine(task.FolderPath, "prompt.md");
        var previousPrompt = File.Exists(promptPath) ? File.ReadAllText(promptPath) : null;
        var previousContextMode = task.ContextMode;

        // The prompt note and the intent are written before the lane move so a
        // claim can never observe the card in Ready without its finishing
        // instruction.
        if (!_mutations.AppendContinuationNote(task.Id, prompt, task.WatchPath))
            return Failed("The continuation instruction could not be appended to the task prompt.");

        var intent = _mutations.SavePendingIntent(
            task.Id,
            ContinueModes.Steer,
            prompt,
            reason: cause.Reason,
            activeJobId: null,
            watchPath: task.WatchPath);
        if (intent is null)
            return Failed("The continuation intent could not be persisted.");

        // A salvaged worktree leaves no resumable session behind, so the round
        // starts from a clean CLI context rather than whatever the previous one
        // left on the host.
        _mutations.SetContextModeOnFolder(task.FolderPath, CliContextModes.Clean);

        // A round whose previous agent is gone cannot fetch the salvage itself,
        // so the claim hands the ref to the runner and the next worktree is
        // prepared on the rescued commit.
        var previousBase = ContinuationBaseStore.Read(task.FolderPath);
        if (cause.StartsFromSalvage && !ContinuationBaseStore.Save(task.FolderPath,
                new ContinuationBaseRecord(
                    salvage.Branch.StartsWith("refs/heads/", StringComparison.Ordinal)
                        ? salvage.Branch
                        : $"refs/heads/{salvage.Branch}",
                    salvage.CommitSha,
                    cause.Reason,
                    attemptId,
                    DateTime.UtcNow)))
        {
            _mutations.DiscardPendingIntent(task.FolderPath);
            _mutations.RestorePromptOnFolder(task.FolderPath, previousPrompt);
            _mutations.RestoreContextModeOnFolder(task.FolderPath, previousContextMode);
            return Failed("The salvage continuation base could not be persisted.");
        }

        var move = await _transitions.MoveAsync(
            task.Id,
            TaskStates.Ready,
            task.WatchPath,
            ct,
            cause: $"{cause.LaneCause}:{round}/{RunTimeoutSalvageContinuationPolicy.MaxAutomaticContinuationRounds}",
            authorityWrite: authorityWrite,
            suppressProductExecution: true,
            transitionCause: LaneChangeCauses.RunnerRequeue,
            transitionDetail: cause.Reason);
        if (move.Status != MoveJobStatus.Success)
        {
            _mutations.DiscardPendingIntent(task.FolderPath);
            if (cause.StartsFromSalvage)
            {
                if (previousBase is null) ContinuationBaseStore.Clear(task.FolderPath);
                else ContinuationBaseStore.Save(task.FolderPath, previousBase);
            }
            if (!_mutations.RestorePromptOnFolder(task.FolderPath, previousPrompt))
            {
                _logger.LogWarning(
                    "continuation-prompt-rollback-failed task={TaskKey} attempt={AttemptId} folder={Folder}",
                    task.TaskKey,
                    attemptId,
                    task.FolderPath);
            }
            if (!_mutations.RestoreContextModeOnFolder(task.FolderPath, previousContextMode))
            {
                _logger.LogWarning(
                    "continuation-context-mode-rollback-failed task={TaskKey} attempt={AttemptId} folder={Folder}",
                    task.TaskKey,
                    attemptId,
                    task.FolderPath);
            }
            return Failed(
                $"The continuation round was prepared, but the lane move was refused: {move.Status} {move.Message}");
        }

        var position = _states.PromoteToReadyTop(
            task.Id,
            task.WatchPath,
            transitionCause: LaneChangeCauses.RunnerRequeue,
            transitionDetail: cause.Reason);
        var queued = _scanner.FindJob(task.Id, task.WatchPath);
        var queuedFolder = queued?.FolderPath ?? move.NewFolderPath ?? task.FolderPath;

        var receipt = CardRoundBudgetReceipt.Details(
            cardBudget with { Used = cardBudget.Used + 1 }, [], "automatic-continuation");
        receipt["automatic"] = "true";
        receipt["attemptEpoch"] = Invariant(epoch);
        receipt["reason"] = cause.Reason;
        receipt["round"] = Invariant(round);
        receipt["maximumRounds"] = Invariant(RunTimeoutSalvageContinuationPolicy.MaxAutomaticContinuationRounds);
        receipt["salvageBranch"] = salvage.Branch;
        receipt["salvageCommitSha"] = salvage.CommitSha;
        receipt["mode"] = ContinueModes.Steer;
        receipt["contextMode"] = CliContextModes.Clean;
        receipt["runAttemptId"] = attemptId;

        _timeline.Append(
            queuedFolder,
            TimelineEventKinds.ContinuationRoundStarted,
            TimelineActors.System,
            $"Automatically started a continuation round to finish the salvaged work ({salvage.Describe()}).",
            runId: attemptId,
            payloadRef: "prompt.md",
            details: receipt);
        _logger.LogInformation(
            "continuation-round-started project={Project} task={TaskKey} attempt={AttemptId} epoch={Epoch} round={Round}/{MaximumRounds} salvage={SalvageBranch}@{SalvageSha} position={Position}",
            task.ProjectName,
            task.TaskKey,
            attemptId,
            epoch,
            round,
            RunTimeoutSalvageContinuationPolicy.MaxAutomaticContinuationRounds,
            salvage.Branch,
            salvage.CommitSha,
            position);

        return new RunTimeoutContinuationResult(true, prompt, round, epoch);
    }

    internal static string BuildPrompt(
        TaskInfo task,
        RunSalvageReference salvage,
        RunContinuationCause cause)
    {
        var evidence = string.IsNullOrWhiteSpace(cause.Evidence)
            ? string.Empty
            : $"{cause.Evidence!.Trim()} ";
        return
            "## STEER\n\n"
            + $"Continuation round for {task.Key ?? task.Id}. "
            + $"Your previous round {cause.Describe()}; the worktree was salvaged as "
            + $"'{salvage.Branch}' at {salvage.CommitSha}. The work exists, it only lacks its finishing round. "
            + evidence
            + $"Fetch '{salvage.Branch}', continue from {salvage.CommitSha}, and finish: rebase onto the current "
            + "integration branch, resolve every conflict conservatively without dropping the salvaged changes, "
            + "build, run the tests that cover the touched code, write results/status.md, and deliver. "
            + $"Budget your time: this is the last automatic round, so a second {cause.RepeatNoun} parks the card for an operator.";
    }

    private static RunTimeoutContinuationResult Failed(string reason)
        => new(false, reason);

    private static string Invariant(int value)
        => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
