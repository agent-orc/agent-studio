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
/// <item>Fix round: the Remote Review finding-round material
/// (<see cref="V1ReviewPlaneEndpoints"/>): continuation note on
/// <c>prompt.md</c>, versioned orchestrator follow-up and the reissue tag,
/// prepared before the guarded move to the top of Ready and rolled back when
/// the move is refused (<see cref="FixRoundTransaction"/>).</item>
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
    private readonly TaskScannerService _scanner;
    private readonly ILogger<OperatorSweepActions> _logger;

    public OperatorSweepActions(
        TaskTransitionService transitions,
        TaskMutationService mutations,
        TaskFailureContinuationService failureContinuations,
        TaskRunnerService runner,
        TaskScannerService scanner,
        ILogger<OperatorSweepActions> logger)
    {
        _transitions = transitions;
        _mutations = mutations;
        _failureContinuations = failureContinuations;
        _runner = runner;
        _scanner = scanner;
        _logger = logger;
    }

    public Task<OperatorSweepActionResult> OpenFixRoundAsync(
        TaskInfo job, OperatorSweepFixRound round, CancellationToken ct)
        => FixRoundTransaction.RunAsync(
            job.FolderPath,
            job.Id,
            round,
            appendNote: () => _mutations.AppendContinuationNote(job.Id, round.FollowUp, job.WatchPath),
            restorePrompt: previous => _mutations.RestorePromptOnFolder(job.FolderPath, previous),
            moveAsync: token => _transitions.MoveAsync(
                job.Id,
                TaskStates.Ready,
                job.WatchPath,
                token,
                cause: $"operator-sweep:{OperatorSweepKinds.FixRounds}:{round.ReviewAttemptId}",
                reason: $"ProductFailure review {round.ReviewAttemptId} turned into a fix round by the operator sweep.",
                suppressProductExecution: true,
                expectedSourceState: TaskStates.HumanReview,
                transitionCause: LaneChangeCauses.QualityLoop,
                transitionDetail: "operator-sweep-fix-round"),
            stillInSourceLane: () => _scanner.FindJob(job.Id, job.WatchPath) is { State: TaskStates.HumanReview } current
                                     && string.Equals(current.FolderPath, job.FolderPath, StringComparison.OrdinalIgnoreCase),
            _logger,
            ct);

    /// <summary>
    /// The fix round as one recoverable step. Everything the next run needs
    /// (continuation note on <c>prompt.md</c>, orchestrator follow-up, reissue
    /// tag) is written in the Human Review folder first and checked; only then
    /// the guarded lane move carries the folder to Ready. A failed preparation
    /// or a refused move restores the folder, so the card stays in Human Review
    /// and the next tick retries it. A Ready card therefore never lacks its fix
    /// instructions, which the sweep could not repair later because it only
    /// scans the decision lanes.
    /// </summary>
    internal static class FixRoundTransaction
    {
        internal const string FollowUpFileName = "orchestrator-follow-up.md";

        public static async Task<OperatorSweepActionResult> RunAsync(
            string folderPath,
            string jobId,
            OperatorSweepFixRound round,
            Func<bool> appendNote,
            Func<string?, bool> restorePrompt,
            Func<CancellationToken, Task<MoveJobOutcome>> moveAsync,
            Func<bool> stillInSourceLane,
            ILogger logger,
            CancellationToken ct)
        {
            var promptPath = Path.Combine(folderPath, "prompt.md");
            var followUpPath = Path.Combine(folderPath, FollowUpFileName);
            var previousPrompt = File.Exists(promptPath) ? File.ReadAllText(promptPath) : null;
            var previousFollowUp = File.Exists(followUpPath) ? File.ReadAllText(followUpPath) : null;
            var previousTags = ReadTags(folderPath);
            string? historyPath = null;

            OperatorSweepActionResult RollBack(string refusal)
            {
                if (!restorePrompt(previousPrompt))
                    logger.LogWarning("operator-sweep-fix-round-prompt-rollback-failed job={JobId} folder={Folder}", jobId, folderPath);
                try
                {
                    if (previousFollowUp is null) File.Delete(followUpPath);
                    else File.WriteAllText(followUpPath, previousFollowUp);
                    if (historyPath is not null) File.Delete(historyPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "operator-sweep-fix-round-follow-up-rollback-failed job={JobId} folder={Folder}", jobId, folderPath);
                }
                if (previousTags is not null && !TaskJsonFile.UpdateField(folderPath, "tags", previousTags, logger))
                    logger.LogWarning("operator-sweep-fix-round-tag-rollback-failed job={JobId} folder={Folder}", jobId, folderPath);
                return new OperatorSweepActionResult(false, refusal);
            }

            MoveJobOutcome moved;
            try
            {
                if (!appendNote())
                    return RollBack("The fix-round instruction could not be appended to the task prompt.");

                historyPath = await ReviewDecisionOrchestrator.WriteFollowUpFilesAsync(
                    folderPath,
                    round.FollowUp,
                    new ReviewDecisionOrchestrator.SteeringContext(
                        "operator-sweep-fix-round",
                        "reissue",
                        0,
                        Reason: $"Remote Review {round.ReviewAttemptId}"),
                    jobId,
                    logger,
                    ct).ConfigureAwait(false);
                if (historyPath is null || !File.Exists(followUpPath)
                    || !File.ReadAllText(followUpPath).Contains(round.FollowUp.Trim(), StringComparison.Ordinal))
                    return RollBack("The fix-round follow-up could not be written.");

                ConcernTagWriter.MergeConcernTags(folderPath, [ReviewDecisionOrchestrator.ReissueTagId], logger);
                if (ReadTags(folderPath)?.Contains(ReviewDecisionOrchestrator.ReissueTagId, StringComparer.OrdinalIgnoreCase) != true)
                    return RollBack("The reissue tag could not be written.");

                moved = await moveAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A move that threw may still have happened; the folder is only
                // restored while the card verifiably sits where it started.
                if (stillInSourceLane()) RollBack($"{ex.GetType().Name}: {ex.Message}");
                throw;
            }

            if (moved.Status != MoveJobStatus.Success)
                return RollBack($"move to Ready refused: {moved.Status} {moved.Message}".Trim());

            // Cosmetic: the round goes to the top of Ready. The instructions
            // already travelled with the folder, so a failure here is harmless.
            TaskJsonFile.UpdateOrder(moved.NewFolderPath ?? folderPath, 0, logger);
            return new OperatorSweepActionResult(true, $"Fix round opened from review {round.ReviewAttemptId}.");
        }

        internal static List<string>? ReadTags(string folderPath)
        {
            var path = Path.Combine(folderPath, "task.json");
            if (!File.Exists(path)) return null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("tags", out var tags)
                    || tags.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return [];
                return tags.EnumerateArray()
                    .Where(tag => tag.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(tag => tag.GetString()!)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                return null;
            }
        }
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
            : new OperatorSweepActionResult(false, result.Error
                ?? $"Continuation saved ({result.SavedReason}) but the Ready move did not complete.");
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
