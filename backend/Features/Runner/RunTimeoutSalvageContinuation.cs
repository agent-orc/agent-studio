namespace AgentStudio.Runner;

/// <summary>
/// The salvage commit a remote runner transferred before it tore its worktree
/// down. Both the ref and the commit SHA are required: a ref without a commit
/// names no work, and a commit without a ref cannot be fetched by the next
/// round.
/// </summary>
public sealed record RunSalvageReference(string Branch, string CommitSha)
{
    /// <summary>
    /// Picks the salvage pair the next round should start from. A divergent
    /// salvage parks the run's work on its recovery branch, so that pair wins
    /// over the canonical one when it is complete.
    /// </summary>
    public static RunSalvageReference? From(
        string? branch,
        string? commitSha,
        string? recoveryBranch,
        string? recoveryCommitSha)
        => Pair(recoveryBranch, recoveryCommitSha) ?? Pair(branch, commitSha);

    /// <summary>Operator-readable "&lt;ref&gt; at &lt;sha&gt;" form.</summary>
    public string Describe() => $"{Branch} at {CommitSha}";

    private static RunSalvageReference? Pair(string? branch, string? commitSha)
    {
        var normalizedBranch = Normalize(branch);
        var normalizedSha = Normalize(commitSha);
        return normalizedBranch is null || normalizedSha is null
            ? null
            : new RunSalvageReference(normalizedBranch, normalizedSha);
    }

    private static string? Normalize(string? value)
    {
        var normalized = (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return normalized.Length == 0 ? null : normalized;
    }
}

/// <summary>
/// What a remote completion that ended without a recognized terminal outcome
/// does next.
/// </summary>
public enum RunTimeoutSalvageAction
{
    /// <summary>The completion is not the non-terminal class this policy owns.</summary>
    None,

    /// <summary>One automatic continuation round finishes the salvaged work.</summary>
    StartContinuation,

    /// <summary>Park the card, naming the salvage when one exists.</summary>
    Escalate,
}

/// <summary>
/// Pure continuation policy for a run that ended without a recognized terminal
/// outcome (the observed case is a hit run timeout). A run whose worktree was
/// salvaged already produced a delivery; it only lacks its finishing round, so
/// it receives one automatic continuation per delivery generation. A repeat in
/// the same generation escalates, because a second timeout is a problem an
/// operator has to see rather than an unbounded coding loop.
/// </summary>
public static class RunTimeoutSalvageContinuationPolicy
{
    /// <summary>
    /// The completion outcome a runner reports when its run ended without a
    /// terminal sentinel. Its reported reason names the typed cause
    /// (<c>Timeout</c> for the run-timeout case).
    /// </summary>
    public const string NonTerminalOutcome = "unknown";

    /// <summary>Recorded on the timeline entry and on the saved continuation intent.</summary>
    public const string ContinuationReason = "run-timeout-with-salvage";

    public const int MaxAutomaticContinuationRounds = 1;

    /// <summary>
    /// Whether the completion is the class this policy owns: a run that ended
    /// without a recognized terminal outcome. A terminal agent statement
    /// (<c>Blocked</c>, <c>NeedsInput</c>) is the agent's own conclusion and is
    /// never continued automatically.
    /// </summary>
    public static bool IsNonTerminalOutcome(string? outcome)
        => string.Equals(
            (outcome ?? string.Empty).Trim(),
            NonTerminalOutcome,
            StringComparison.OrdinalIgnoreCase);

    public static RunTimeoutSalvageAction Decide(
        string? outcome,
        bool hasSalvageCommit,
        int automaticContinuationRoundsUsed)
    {
        if (!IsNonTerminalOutcome(outcome)) return RunTimeoutSalvageAction.None;

        if (!hasSalvageCommit) return RunTimeoutSalvageAction.Escalate;

        return Math.Max(0, automaticContinuationRoundsUsed) < MaxAutomaticContinuationRounds
            ? RunTimeoutSalvageAction.StartContinuation
            : RunTimeoutSalvageAction.Escalate;
    }

    /// <summary>
    /// The escalation sentence for a non-terminal outcome. It names the salvage
    /// ref and SHA when one exists and the spent continuation rounds when the
    /// budget was already used, so the manual recovery path needs no journal
    /// reading.
    /// </summary>
    public static string ComposeEscalationReason(
        string? reportedReason,
        RunSalvageReference? salvage,
        int automaticContinuationRoundsUsed)
    {
        var reason = (reportedReason ?? string.Empty).Trim();
        var text = reason.Length == 0
            ? "The remote runner ended without a recognized terminal outcome."
            : $"The remote runner ended without a recognized terminal outcome: {reason}";
        if (salvage is null) return text;

        text = text.TrimEnd('.');
        text += $"; salvaged as {salvage.Describe()}";
        var rounds = Math.Max(0, automaticContinuationRoundsUsed);
        if (rounds > 0)
        {
            text += rounds == 1
                ? "; 1 automatic continuation round was already spent"
                : $"; {rounds} automatic continuation rounds were already spent";
        }
        return text + ".";
    }
}

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
/// of the integration branch. Timeouts keep their AGT-2861 behaviour and start
/// on the integration branch, because their agent is alive to fetch the ref
/// itself; a lost worker's round starts on the rescued work.
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
            "timeout");

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
/// on the timeline. The service never authors Git history and never decides the
/// budget itself.
/// </summary>
public sealed class RunTimeoutContinuationService
{
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly TaskStateMachine _states;
    private readonly TaskTransitionService _transitions;
    private readonly TimelineLog _timeline;
    private readonly ILogger<RunTimeoutContinuationService> _logger;

    public RunTimeoutContinuationService(
        TaskScannerService scanner,
        TaskMutationService mutations,
        TaskStateMachine states,
        TaskTransitionService transitions,
        TimelineLog timeline,
        ILogger<RunTimeoutContinuationService> logger)
    {
        _scanner = scanner;
        _mutations = mutations;
        _states = states;
        _transitions = transitions;
        _timeline = timeline;
        _logger = logger;
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
        var epoch = OperatorReviewRequeueService.ReadEpoch(task.FolderPath);
        var round = CountAutomaticRounds(task) + 1;
        cause ??= RunContinuationCause.Timeout(reportedReason);
        var prompt = BuildPrompt(task, salvage, cause);

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
        if (cause.StartsFromSalvage)
        {
            ContinuationBaseStore.Save(task.FolderPath, new ContinuationBaseRecord(
                salvage.Branch.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? salvage.Branch
                    : $"refs/heads/{salvage.Branch}",
                salvage.CommitSha,
                cause.Reason,
                attemptId,
                DateTime.UtcNow));
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

        _timeline.Append(
            queuedFolder,
            TimelineEventKinds.ContinuationRoundStarted,
            TimelineActors.System,
            $"Automatically started a continuation round to finish the salvaged work ({salvage.Describe()}).",
            runId: attemptId,
            payloadRef: "prompt.md",
            details: new Dictionary<string, string>
            {
                ["automatic"] = "true",
                ["attemptEpoch"] = Invariant(epoch),
                ["reason"] = cause.Reason,
                ["round"] = Invariant(round),
                ["maximumRounds"] = Invariant(
                    RunTimeoutSalvageContinuationPolicy.MaxAutomaticContinuationRounds),
                ["salvageBranch"] = salvage.Branch,
                ["salvageCommitSha"] = salvage.CommitSha,
                ["mode"] = ContinueModes.Steer,
                ["contextMode"] = CliContextModes.Clean,
                ["runAttemptId"] = attemptId,
            });
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
