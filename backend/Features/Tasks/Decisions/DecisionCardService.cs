using AgentStudio.Runner;

namespace AgentStudio.Tasks;

/// <summary>Card lifecycle binding for the shared decision record service.</summary>
public sealed class DecisionCardService
{
    private static readonly SemaphoreSlim WriteGate = new(1, 1);
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly TaskTransitionService _transitions;
    private readonly TimelineLog _timeline;
    private readonly DecisionRecordService _records;
    private readonly OrchestratorLog _activityFeed;
    private readonly ILogger<DecisionCardService> _logger;

    private DecisionRecordWriteResult WriteRecord(TaskInfo card, DecisionContent decision)
    {
        var key = card.Key ?? card.Id;
        return _records.Write(card.ProjectName,
            $"{WikiProducerTargets.DecisionsFolder}/{key}.md",
            new DecisionRecord(key, card.Title, decision));
    }

    public DecisionCardService(TaskScannerService scanner, TaskMutationService mutations,
        TaskTransitionService transitions, TimelineLog timeline,
        ILogger<DecisionCardService> logger, DecisionRecordService records,
        OrchestratorLog activityFeed)
    {
        _scanner = scanner;
        _mutations = mutations;
        _transitions = transitions;
        _timeline = timeline;
        _logger = logger;
        _records = records;
        _activityFeed = activityFeed;
    }

    public async Task<DecisionCardOutcome> DecideAsync(string jobId, string? watchPath,
        DecideCardRequest req, string decidedBy, CancellationToken ct = default, string? actorRole = null)
    {
        await WriteGate.WaitAsync(ct);
        try { return await DecideCoreAsync(jobId, watchPath, req, decidedBy, ct, actorRole); }
        finally { WriteGate.Release(); }
    }

    private async Task<DecisionCardOutcome> DecideCoreAsync(string jobId, string? watchPath,
        DecideCardRequest req, string decidedBy, CancellationToken ct, string? actorRole)
    {
        var card = _scanner.FindJob(jobId, watchPath);
        if (card is null) return new(DecisionCardStatus.NotFound);
        if (!TaskKinds.IsDecision(card.Kind) || card.Decision is null)
            return new(DecisionCardStatus.NotDecision);
        if (!DecisionCardPolicy.MayDecide(card.Decision, decidedBy, actorRole))
            return new(DecisionCardStatus.Forbidden, Message: "This decision is assigned to another client or role.");
        var errors = DecisionCardPolicy.ValidateChoice(card.Decision, req?.OptionId, req?.Rationale);
        if (errors.Count > 0)
            return errors.Any(error => error.Code == DecisionCardErrorCode.NotOpen)
                ? new(DecisionCardStatus.Conflict, Errors: errors, Message: errors[0].Message)
                : new(DecisionCardStatus.InvalidRequest, Errors: errors);

        var now = DateTime.UtcNow;
        var optionId = card.Decision.Options.First(option =>
            string.Equals(option.Id.Trim(), req.OptionId!.Trim(), StringComparison.OrdinalIgnoreCase)).Id.Trim();
        var actor = string.IsNullOrWhiteSpace(decidedBy) ? DecisionDeciders.Operator : decidedBy.Trim();
        var entry = new DecisionHistoryEntry(DecisionStatuses.Decided, optionId,
            req.Rationale?.Trim(), actor, now, null);
        var decided = DecisionCardPolicy.Decide(card.Decision, optionId,
            req.Rationale ?? string.Empty, actor, now) with
        {
            History = [.. card.Decision.History, entry],
        };
        var record = WriteRecord(card, decided);
        if (!record.Success)
            return new(DecisionCardStatus.Conflict, Message: record.Error);
        decided = decided with { RecordPath = record.Path };
        if (!_mutations.SetDecisionContent(jobId, decided, watchPath))
        {
            WriteRecord(card, card.Decision);
            return new(DecisionCardStatus.Conflict, Message: "Decision card could not be updated.");
        }
        var move = await _transitions.MoveAsync(jobId, TaskStates.Completed, watchPath, ct,
            cause: TimelineActors.Human(actor), reason: "Decision recorded by the named decider",
            operatorOverride: true);
        if (move.Status != MoveJobStatus.Success)
        {
            _mutations.SetDecisionContent(jobId, card.Decision, watchPath);
            WriteRecord(card, card.Decision);
            return new(DecisionCardStatus.Conflict, Message: move.Message);
        }
        var moved = _scanner.FindJob(jobId, watchPath)!;
        _timeline.Append(moved.FolderPath, TimelineEventKinds.DecisionDecided,
            TimelineActors.Human(actor), summary: $"Decided: {DecisionCardPolicy.ChosenOption(decided)?.Label ?? optionId}",
            payloadRef: record.Path,
            details: new() { ["optionId"] = optionId, ["rationale"] = req.Rationale?.Trim() ?? "" });
        _activityFeed.Append(card.WatchPath, new OrchestratorLogEntry
        {
            Kind = OrchestratorLogKinds.Decision,
            Topic = OrchestratorLogTopics.DecisionCard,
            Summary = $"Decision decided: {card.Key ?? card.Id} chose {optionId}",
            Reasoning = req.Rationale?.Trim(),
            JobId = jobId,
        });
        _logger.LogInformation("decision-decided job={JobId} option={OptionId}", jobId, optionId);
        return new(DecisionCardStatus.Success, decided, TaskStates.Completed);
    }

    public async Task<DecisionCardOutcome> ReopenAsync(string jobId, string? watchPath,
        ReopenDecisionRequest? req, string actor, CancellationToken ct = default, string? actorRole = null)
    {
        await WriteGate.WaitAsync(ct);
        try { return await ReopenCoreAsync(jobId, watchPath, req, actor, ct, actorRole); }
        finally { WriteGate.Release(); }
    }

    private async Task<DecisionCardOutcome> ReopenCoreAsync(string jobId, string? watchPath,
        ReopenDecisionRequest? req, string actor, CancellationToken ct, string? actorRole)
    {
        var card = _scanner.FindJob(jobId, watchPath);
        if (card is null) return new(DecisionCardStatus.NotFound);
        if (!TaskKinds.IsDecision(card.Kind) || card.Decision is null)
            return new(DecisionCardStatus.NotDecision);
        if (!DecisionCardPolicy.MayDecide(card.Decision, actor, actorRole))
            return new(DecisionCardStatus.Forbidden, Message: "This decision is assigned to another client or role.");
        var errors = DecisionCardPolicy.ValidateReopen(card.Decision, req?.Note);
        if (errors.Count > 0)
            return errors.Any(error => error.Code == DecisionCardErrorCode.NotDecided)
                ? new(DecisionCardStatus.Conflict, Errors: errors, Message: errors[0].Message)
                : new(DecisionCardStatus.InvalidRequest, Errors: errors);
        var by = string.IsNullOrWhiteSpace(actor) ? DecisionDeciders.Operator : actor.Trim();
        var reopened = DecisionCardPolicy.Reopen(card.Decision, req?.Note) with
        {
            History = [.. card.Decision.History,
                new DecisionHistoryEntry(DecisionStatuses.Reopened, null, null, by,
                    DateTime.UtcNow, req?.Note?.Trim())],
        };
        var record = WriteRecord(card, reopened);
        if (!record.Success)
            return new(DecisionCardStatus.Conflict, Message: record.Error);
        reopened = reopened with { RecordPath = record.Path };
        var move = await _transitions.MoveAsync(jobId, TaskStates.Preparation, watchPath, ct,
            cause: TimelineActors.Human(by), reason: "Decision reopened");
        if (move.Status != MoveJobStatus.Success)
        {
            WriteRecord(card, card.Decision);
            return new(DecisionCardStatus.Conflict, Message: move.Message);
        }
        if (!_mutations.SetDecisionContent(jobId, reopened, watchPath))
        {
            await _transitions.MoveAsync(jobId, TaskStates.Completed, watchPath, ct,
                cause: TimelineActors.Human(by), reason: "Restore decided card after reopen failed",
                operatorOverride: true);
            WriteRecord(card, card.Decision);
            return new(DecisionCardStatus.Conflict, Message: "Decision card could not be updated.");
        }
        var moved = _scanner.FindJob(jobId, watchPath)!;
        _timeline.Append(moved.FolderPath, TimelineEventKinds.DecisionReopened,
            TimelineActors.Human(by), summary: string.IsNullOrWhiteSpace(req?.Note)
                ? "Decision reopened" : $"Decision reopened: {req.Note.Trim()}",
            payloadRef: record.Path);
        _activityFeed.Append(card.WatchPath, new OrchestratorLogEntry
        {
            Kind = OrchestratorLogKinds.Decision,
            Topic = OrchestratorLogTopics.DecisionCard,
            Summary = $"Decision reopened: {card.Key ?? card.Id}",
            Reasoning = req?.Note?.Trim(),
            JobId = jobId,
        });
        _logger.LogInformation("decision-reopened job={JobId}", jobId);
        return new(DecisionCardStatus.Success, reopened, TaskStates.Preparation);
    }
}
