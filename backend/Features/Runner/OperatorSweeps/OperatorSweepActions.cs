using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <param name="Started">True when the round was opened (the card left its decision lane or a follow-up was queued).</param>
/// <param name="Detail">Operator-facing detail: the prompt headline on success, the refusal on failure.</param>
public sealed record OperatorSweepActionResult(bool Started, string Detail);

/// <summary>Everything a fix round needs, read by the service from the settled review.</summary>
public sealed record OperatorSweepFixRound(
    string ReviewAttemptId,
    string FollowUp);

/// <summary>
/// The bounded side effects the sweeps may apply. Each one is the internal path
/// the product already uses for the same move; none goes through the public
/// HTTP API. A seam so the decision flow is testable without a runner.
/// </summary>
public interface IOperatorSweepActions
{
    Task<OperatorSweepActionResult> OpenFixRoundAsync(TaskInfo job, OperatorSweepFixRound round, CancellationToken ct);

    Task<OperatorSweepActionResult> ContinueGateFailureAsync(TaskInfo job, string subjectKey, CancellationToken ct);

    Task<OperatorSweepActionResult> ContinueSalvageAsync(TaskInfo job, OperatorSweepSalvageFacts salvage, CancellationToken ct);
}

/// <summary>
/// Production side effects.
/// <list type="bullet">
/// <item>Fix round: the Remote Review finding-round sequence
/// (<see cref="V1ReviewPlaneEndpoints"/>): guarded move to the top of Ready,
/// continuation note on <c>prompt.md</c>, versioned orchestrator follow-up, and
/// the reissue tag.</item>
/// <item>Gate triage: <see cref="TaskFailureContinuationService"/>, the same
/// service the operator failure panel calls.</item>
/// <item>Salvage: the continuation base the lost-worker path writes, plus the
/// AGT-2861 continuation prompt, queued through
/// <see cref="TaskRunnerService.ContinueJobAsync"/>.</item>
/// </list>
/// </summary>
public sealed class OperatorSweepActions : IOperatorSweepActions
{
    private readonly TaskTransitionService _transitions;
    private readonly TaskMutationService _mutations;
    private readonly TaskFailureContinuationService _failureContinuations;
    private readonly TaskRunnerService _runner;
    private readonly ILogger<OperatorSweepActions> _logger;

    public OperatorSweepActions(
        TaskTransitionService transitions,
        TaskMutationService mutations,
        TaskFailureContinuationService failureContinuations,
        TaskRunnerService runner,
        ILogger<OperatorSweepActions> logger)
    {
        _transitions = transitions;
        _mutations = mutations;
        _failureContinuations = failureContinuations;
        _runner = runner;
        _logger = logger;
    }

    public async Task<OperatorSweepActionResult> OpenFixRoundAsync(
        TaskInfo job, OperatorSweepFixRound round, CancellationToken ct)
    {
        var moved = await _transitions.MoveAsync(
            job.Id,
            TaskStates.Ready,
            job.WatchPath,
            ct,
            cause: $"operator-sweep:{OperatorSweepKinds.FixRounds}:{round.ReviewAttemptId}",
            reason: $"ProductFailure review {round.ReviewAttemptId} turned into a fix round by the operator sweep.",
            suppressProductExecution: true,
            expectedSourceState: TaskStates.HumanReview,
            transitionCause: LaneChangeCauses.QualityLoop,
            transitionDetail: "operator-sweep-fix-round").ConfigureAwait(false);
        if (moved.Status != MoveJobStatus.Success)
            return new OperatorSweepActionResult(false, $"move to Ready refused: {moved.Status} {moved.Message}".Trim());

        var movedPath = moved.NewFolderPath ?? job.FolderPath;
        TaskJsonFile.UpdateOrder(movedPath, 0, _logger);
        _mutations.AppendContinuationNote(job.Id, round.FollowUp, job.WatchPath);
        await ReviewDecisionOrchestrator.WriteFollowUpFilesAsync(
            movedPath,
            round.FollowUp,
            new ReviewDecisionOrchestrator.SteeringContext(
                "operator-sweep-fix-round",
                "reissue",
                0,
                Reason: $"Remote Review {round.ReviewAttemptId}"),
            job.Id,
            _logger,
            ct).ConfigureAwait(false);
        ConcernTagWriter.MergeConcernTags(movedPath, [ReviewDecisionOrchestrator.ReissueTagId], _logger);
        return new OperatorSweepActionResult(true, $"Fix round opened from review {round.ReviewAttemptId}.");
    }

    public async Task<OperatorSweepActionResult> ContinueGateFailureAsync(
        TaskInfo job, string subjectKey, CancellationToken ct)
    {
        var result = await _failureContinuations.ContinueAsync(
            job,
            $"operator-sweep:{OperatorSweepKinds.GateTriage}",
            automatic: true,
            ct,
            new Dictionary<string, string>
            {
                [OperatorSweepTriggers.ReceiptSweepKey] = OperatorSweepKinds.GateTriage,
                [OperatorSweepTriggers.ReceiptSubjectKey] = subjectKey,
            }).ConfigureAwait(false);
        return result.Started
            ? new OperatorSweepActionResult(true, $"Fix round queued from {result.Stage} ({result.RunStatus}).")
            : new OperatorSweepActionResult(false, result.Error ?? "The failure continuation was refused.");
    }

    public async Task<OperatorSweepActionResult> ContinueSalvageAsync(
        TaskInfo job, OperatorSweepSalvageFacts salvage, CancellationToken ct)
    {
        var reference = salvage.Salvage;
        // The base travels with the folder through the lane move, so the next
        // worktree is prepared on the rescued commit, exactly as a lost-worker
        // continuation does it.
        if (!ContinuationBaseStore.Save(job.FolderPath, new ContinuationBaseRecord(
                reference.Branch.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? reference.Branch
                    : $"refs/heads/{reference.Branch}",
                reference.CommitSha,
                "operator-sweep-salvage",
                salvage.RunAttemptId,
                DateTime.UtcNow)))
            return new OperatorSweepActionResult(false, "The continuation base could not be written.");

        var prompt = RunTimeoutContinuationService.BuildPrompt(
            job,
            reference,
            RunContinuationCause.Timeout(salvage.ReportedReason) with { StartsFromSalvage = true });
        try
        {
            var response = await _runner.ContinueJobAsync(
                job.Id,
                prompt,
                job.WatchPath,
                mode: ContinueModes.Extend,
                ct: ct,
                reason: $"Operator sweep continued the salvage {reference.Describe()}.",
                triggeredBy: "operator-sweep").ConfigureAwait(false);
            return new OperatorSweepActionResult(true, $"Continued from {reference.Describe()} ({response.Status}).");
        }
        catch (TaskOperationException ex)
        {
            ContinuationBaseStore.Clear(job.FolderPath);
            return new OperatorSweepActionResult(false, ex.Message);
        }
    }

    /// <summary>
    /// The fix-round prompt: the Remote Review finding template plus the
    /// verification commands the review ran or planned, and the budget line.
    /// </summary>
    internal static string BuildFixRoundFollowUp(
        string reviewAttemptId,
        Contract.ReviewReportRequest? report,
        ReviewAttemptDto? attempt,
        int round,
        int allowed)
    {
        var findings = (report?.Verdicts ?? [])
            .Where(verdict => !string.Equals(verdict.Status, "pass", StringComparison.OrdinalIgnoreCase))
            .Select(verdict => new Contract.ReviewFollowUpFinding(
                verdict.Aspect,
                verdict.Status,
                verdict.Summary,
                verdict.EvidenceChecked,
                verdict.Missing,
                verdict.Classification))
            .ToList();
        if (findings.Count == 0)
        {
            findings.Add(new Contract.ReviewFollowUpFinding(
                "review",
                "block",
                report?.Summary ?? attempt?.TerminalReason ?? attempt?.FailureClassification
                    ?? "The review graded the delivery ProductFailure without a structured finding."));
        }

        var failedCommands = (report?.Commands ?? [])
            .Where(command => command.ExitCode is not 0)
            .Select(command => FormatCommand(command.FileName, command.Arguments))
            .ToList();
        var plannedCommands = (attempt?.Subject.Plan?.Commands ?? [])
            .Where(command => command.Required
                              && string.Equals(command.ExecutionKind, Contract.ReviewCommandKinds.Tool, StringComparison.Ordinal))
            .Select(command => FormatCommand(command.FileName, command.Arguments))
            .ToList();
        var commands = failedCommands.Concat(plannedCommands).Distinct(StringComparer.Ordinal).Take(12).ToList();

        var text = V1ReviewPlaneEndpoints.BuildRemoteFindingFollowUp(findings, reviewAttemptId);
        if (commands.Count > 0)
        {
            text += "\n\n## Verification commands\n\nRun these before you deliver; the review ran them against the delivery.\n\n"
                    + string.Join('\n', commands.Select(command => $"- `{command}`"));
        }
        return text + $"\n\nThis is automatic round {round} of {allowed} for this card. "
                    + "When the budget is spent, the card waits for a person.";
    }

    private static string FormatCommand(string fileName, IReadOnlyList<string> arguments)
        => arguments.Count == 0 ? fileName : $"{fileName} {string.Join(' ', arguments)}";
}
