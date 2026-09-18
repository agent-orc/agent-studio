using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

public enum ProviderRejectionContinuationAction
{
    None,
    StartContinuation,
    Escalate,
}

public sealed record ProviderRejectionContinuationPlan(
    ProviderRejectionContinuationAction Action,
    ProviderRejectionModelFallback? Fallback,
    bool PinCard,
    string Reason);

/// <summary>
/// Pure policy for a provider refusal. A declared sibling may finish salvaged
/// work only when it clears the card's correctness floor. The second refusal
/// of the configured model makes that sibling the card's explicit route.
/// </summary>
public static class ProviderRejectionContinuationPolicy
{
    public const string ContinuationReason = "provider-rejected-request";
    public const int RefusalsBeforePin = 2;

    public static ProviderRejectionContinuationPlan Decide(
        bool isProviderRejection,
        bool hasSalvage,
        ProviderRejectionModelFallback? fallback,
        bool fallbackMeetsFloor,
        int refusalCount,
        bool rejectedRunWasFallback)
    {
        if (!isProviderRejection)
            return new(ProviderRejectionContinuationAction.None, null, false, string.Empty);
        if (!hasSalvage)
            return Escalate("The provider refused the request and the run produced no salvage commit.");
        if (rejectedRunWasFallback)
            return Escalate("The provider also refused the declared sibling model.");
        if (fallback is null)
            return Escalate("No provider-refusal sibling is declared for this model.");
        if (!fallbackMeetsFloor)
            return Escalate("The declared sibling is below this card's correctness floor.");

        return new(
            ProviderRejectionContinuationAction.StartContinuation,
            fallback,
            PinCard: Math.Max(0, refusalCount) >= RefusalsBeforePin,
            Reason: fallback.Reason);
    }

    public static string Describe(ProviderRequestRejection rejection)
    {
        var code = string.IsNullOrWhiteSpace(rejection.Code) ? "request_rejected" : rejection.Code.Trim();
        var parameter = string.IsNullOrWhiteSpace(rejection.Parameter) ? string.Empty : $" {rejection.Parameter.Trim()}";
        return $"{code}{parameter}";
    }

    public static string ComposeEscalationReason(
        ProviderRequestRejection rejection,
        string policyReason)
        => $"Provider refused the request ({Describe(rejection)}): {rejection.Message} {policyReason}".Trim();

    private static ProviderRejectionContinuationPlan Escalate(string reason)
        => new(ProviderRejectionContinuationAction.Escalate, null, false, reason);
}

public sealed record ProviderRejectionContinuationResult(
    bool Started,
    string Reason,
    bool CardPinned = false);

/// <summary>
/// Persists one salvage continuation on the declared sibling. The route lives
/// on the pending intent for the first refusal, so task.json remains unchanged;
/// the second refusal pins the card before it returns to Ready.
/// </summary>
public sealed class ProviderRejectionContinuationService
{
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly TaskStateMachine _states;
    private readonly TaskTransitionService _transitions;
    private readonly TimelineLog _timeline;
    private readonly ILogger<ProviderRejectionContinuationService> _logger;

    public ProviderRejectionContinuationService(
        TaskScannerService scanner,
        TaskMutationService mutations,
        TaskStateMachine states,
        TaskTransitionService transitions,
        TimelineLog timeline,
        ILogger<ProviderRejectionContinuationService> logger)
    {
        _scanner = scanner;
        _mutations = mutations;
        _states = states;
        _transitions = transitions;
        _timeline = timeline;
        _logger = logger;
    }

    public int CountRefusals(TaskInfo task, string model)
        => _timeline.ReadAll(task.FolderPath).Count(entry =>
            entry.Kind == TimelineEventKinds.AgentRunFinished
            && entry.Details?.GetValueOrDefault("typedOutcome") == ExecutionOutcomeKind.ProviderRejectedRequest.ToString()
            && string.Equals(
                entry.Details?.GetValueOrDefault("providerRejectionModel"),
                model,
                StringComparison.OrdinalIgnoreCase));

    public async Task<ProviderRejectionContinuationResult> StartAsync(
        TaskInfo task,
        RunSalvageReference salvage,
        ProviderRejectionModelFallback fallback,
        ProviderRequestRejection rejection,
        string thinkingLevel,
        bool pinCard,
        string attemptId,
        AttemptWriteReference authorityWrite,
        CancellationToken ct)
    {
        var prompt = BuildPrompt(task, salvage, fallback, rejection);
        if (!_mutations.AppendContinuationNote(task.Id, prompt, task.WatchPath))
            return Failed("The provider-refusal continuation could not be appended to the task prompt.");

        var fallbackInfo = new ModelFallbackInfo(
            fallback.FromModel,
            fallback.ToModel,
            fallback.Reason,
            fallback.CliType,
            thinkingLevel);
        if (_mutations.SavePendingIntent(
                task.Id,
                ContinueModes.Steer,
                prompt,
                ProviderRejectionContinuationPolicy.ContinuationReason,
                activeJobId: null,
                watchPath: task.WatchPath,
                modelFallback: fallbackInfo) is null)
            return Failed("The provider-refusal continuation intent could not be persisted.");

        if (pinCard)
        {
            if (!_mutations.SetJobModel(task.Id, fallback.ToModel, task.WatchPath)
                || !_mutations.SetJobThinkingLevel(task.Id, thinkingLevel, task.WatchPath))
            {
                _mutations.DiscardPendingIntent(task.FolderPath);
                return Failed("The sibling route could not be pinned on the card.");
            }
        }

        _mutations.SetContextModeOnFolder(task.FolderPath, CliContextModes.Clean);
        var move = await _transitions.MoveAsync(
            task.Id,
            TaskStates.Ready,
            task.WatchPath,
            ct,
            cause: "provider-rejection-continuation",
            authorityWrite: authorityWrite,
            suppressProductExecution: true,
            transitionCause: LaneChangeCauses.RunnerRequeue,
            transitionDetail: ProviderRejectionContinuationPolicy.ContinuationReason);
        if (move.Status != MoveJobStatus.Success)
        {
            _mutations.DiscardPendingIntent(task.FolderPath);
            return Failed($"The sibling continuation lane move was refused: {move.Status} {move.Message}");
        }

        _states.PromoteToReadyTop(
            task.Id,
            task.WatchPath,
            transitionCause: LaneChangeCauses.RunnerRequeue,
            transitionDetail: ProviderRejectionContinuationPolicy.ContinuationReason);
        var queued = _scanner.FindJob(task.Id, task.WatchPath);
        var folder = queued?.FolderPath ?? move.NewFolderPath ?? task.FolderPath;
        var summary =
            $"Provider refused the request ({ProviderRejectionContinuationPolicy.Describe(rejection)}); continued on {fallback.ToModel}."
            + (pinCard ? $" Card pinned to {fallback.ToModel} after {ProviderRejectionContinuationPolicy.RefusalsBeforePin} refusals." : string.Empty);
        _timeline.Append(
            folder,
            TimelineEventKinds.ProviderRejectionContinuationStarted,
            TimelineActors.System,
            summary,
            runId: attemptId,
            payloadRef: "prompt.md",
            details: new Dictionary<string, string>
            {
                ["reason"] = ProviderRejectionContinuationPolicy.ContinuationReason,
                ["fromModel"] = fallback.FromModel,
                ["toModel"] = fallback.ToModel,
                ["thinkingLevel"] = thinkingLevel,
                ["providerCode"] = rejection.Code ?? string.Empty,
                ["providerParam"] = rejection.Parameter ?? string.Empty,
                ["salvageBranch"] = salvage.Branch,
                ["salvageCommitSha"] = salvage.CommitSha,
                ["cardPinned"] = pinCard ? "true" : "false",
                ["runAttemptId"] = attemptId,
            });
        _logger.LogWarning(
            "provider-rejection-continuation task={TaskKey} attempt={AttemptId} from={FromModel} to={ToModel} code={Code} parameter={Parameter} pinned={Pinned}",
            task.TaskKey,
            attemptId,
            fallback.FromModel,
            fallback.ToModel,
            rejection.Code,
            rejection.Parameter,
            pinCard);
        return new(true, summary, pinCard);
    }

    internal static string BuildPrompt(
        TaskInfo task,
        RunSalvageReference salvage,
        ProviderRejectionModelFallback fallback,
        ProviderRequestRejection rejection)
        =>
            "## STEER\n\n"
            + $"Provider-refusal continuation for {task.Key ?? task.Id}. "
            + $"The provider refused {fallback.FromModel} ({ProviderRejectionContinuationPolicy.Describe(rejection)}). "
            + $"Continue on the declared sibling {fallback.ToModel} at the same thinking level. "
            + $"Fetch '{salvage.Branch}', continue from {salvage.CommitSha}, then build, test, write results/status.md, and deliver.";

    private static ProviderRejectionContinuationResult Failed(string reason) => new(false, reason);
}
