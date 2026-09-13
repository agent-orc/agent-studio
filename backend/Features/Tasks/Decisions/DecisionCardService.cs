using System.Text;

namespace AgentStudio.Tasks;

/// <summary>
/// Application coordinator for the AGT-2795 decision-card lifecycle: recording a
/// choice, reopening a settled decision, and the bounded side effects that
/// follow - the durable ADR-style record, the timeline events, the lane move,
/// and unblocking the implementation cards that depend on the decision.
///
/// <para>The branching itself is a pure decision in
/// <see cref="DecisionCardPolicy"/>; this service reads the card, applies the
/// policy result, and performs the side effects in a fixed order so a partial
/// write cannot look complete, per the .NET backend style guide.</para>
/// </summary>
public sealed class DecisionCardService
{
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly TaskTransitionService _transitions;
    private readonly TimelineLog _timeline;
    private readonly ILogger<DecisionCardService> _logger;

    /// <summary>Relative path of the ADR-style decision record inside the card folder.</summary>
    public const string RecordFileName = "decision-record.md";

    public DecisionCardService(
        TaskScannerService scanner,
        TaskMutationService mutations,
        TaskTransitionService transitions,
        TimelineLog timeline,
        ILogger<DecisionCardService> logger)
    {
        _scanner = scanner;
        _mutations = mutations;
        _transitions = transitions;
        _timeline = timeline;
        _logger = logger;
    }

    /// <summary>
    /// Records the decider's choice: validates it against the card's options,
    /// writes the ADR-style record, persists the settled content, appends the
    /// decided timeline event, unblocks dependants (or seeds an implementation
    /// card from the chosen option), and moves the card to its terminal record
    /// lane.
    /// </summary>
    public async Task<DecisionCardOutcome> DecideAsync(
        string jobId, string? watchPath, DecideCardRequest req, string decidedBy, CancellationToken ct = default)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return new(DecisionCardStatus.NotFound);
        if (!TaskKinds.IsDecision(info.Kind) || info.Decision == null)
            return new(DecisionCardStatus.NotDecision);

        var content = info.Decision;
        var errors = DecisionCardPolicy.ValidateChoice(content, req?.OptionId, req?.Rationale);
        if (errors.Count > 0)
        {
            // An already-settled card is a conflict (409); a bad option / missing
            // rationale is a client input error (400).
            return errors.Any(e => e.Code == DecisionCardErrorCode.NotOpen)
                ? new(DecisionCardStatus.Conflict, Errors: errors, Message: errors[0].Message)
                : new(DecisionCardStatus.InvalidRequest, Errors: errors);
        }

        var now = DateTime.UtcNow;
        var optionId = req!.OptionId!.Trim();
        var rationale = req.Rationale!.Trim();
        var chosen = DecisionCardPolicy.ChosenOption(content with { ChosenOptionId = optionId });

        // Side effect 1: the durable, linkable record (before the state write so
        // a decided card always has its record path point at a real file).
        var recordPath = WriteDecisionRecord(info, content, chosen, rationale, decidedBy, now);

        // Side effect 2: unblock dependants / seed an implementation card. Done
        // before the terminal move so the reference index still sees the card's
        // pre-move lane; the dependants' waits-on gate keys on 6-completed, which
        // the move below satisfies.
        var (unblocked, created) = await UnblockDependantsAsync(info, content, chosen, rationale, decidedBy, watchPath, ct);

        // Side effect 3: persist the settled content (records what it unblocked).
        var decided = DecisionCardPolicy.Decide(content, optionId, rationale, decidedBy, now, recordPath) with
        {
            BlockedCards = unblocked.Concat(created).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
        _mutations.SetDecisionContent(jobId, decided, watchPath);

        // Side effect 4: the decided timeline event.
        _timeline.Append(
            info.FolderPath,
            TimelineEventKinds.DecisionDecided,
            TimelineActors.Human(decidedBy),
            summary: $"Decided: {chosen?.Label ?? optionId}",
            payloadRef: recordPath,
            details: new()
            {
                ["option"] = optionId,
                ["rationale"] = rationale,
                ["unblocked"] = string.Join(",", unblocked),
                ["created"] = string.Join(",", created),
            });

        // Side effect 5: the card becomes a durable record in 6-completed. It is
        // no-branch, so acceptance integration is not required; the override is
        // the explicit one-shot completion the lane move contract requires.
        var move = await _transitions.MoveAsync(
            jobId, TaskStates.Completed, watchPath, ct,
            cause: TimelineActors.Human(decidedBy),
            reason: "Decision recorded",
            operatorOverride: true);
        var targetState = move.Status == MoveJobStatus.Success ? TaskStates.Completed : info.State;

        _logger.LogInformation(
            "decision-decided job={JobId} option={Option} unblocked={Unblocked} created={Created} lane={Lane}",
            jobId, optionId, unblocked.Count, created.Count, targetState);

        return new(DecisionCardStatus.Success, decided, targetState, unblocked, created);
    }

    /// <summary>
    /// Reopens a settled decision: clears the recorded choice, appends the
    /// reopened event, and returns the card to preparation. Dependants re-block
    /// automatically because their waits-on gate keys on the decision reaching a
    /// terminal lane, which the return to preparation undoes.
    /// </summary>
    public async Task<DecisionCardOutcome> ReopenAsync(
        string jobId, string? watchPath, ReopenDecisionRequest? req, string actor, CancellationToken ct = default)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return new(DecisionCardStatus.NotFound);
        if (!TaskKinds.IsDecision(info.Kind) || info.Decision == null)
            return new(DecisionCardStatus.NotDecision);

        var errors = DecisionCardPolicy.ValidateReopen(info.Decision);
        if (errors.Count > 0)
            return new(DecisionCardStatus.Conflict, Errors: errors, Message: errors[0].Message);

        var reopened = DecisionCardPolicy.Reopen(info.Decision, req?.Note);
        _mutations.SetDecisionContent(jobId, reopened, watchPath);

        _timeline.Append(
            info.FolderPath,
            TimelineEventKinds.DecisionReopened,
            TimelineActors.Human(actor),
            summary: string.IsNullOrWhiteSpace(reopened.ReopenNote)
                ? "Decision reopened"
                : $"Decision reopened: {reopened.ReopenNote}");

        // Return the card to preparation so it re-enters the decision surface and
        // its dependants are blocked again by the waits-on gate.
        string targetState = info.State;
        if (info.State != TaskStates.Preparation)
        {
            var move = await _transitions.MoveAsync(
                jobId, TaskStates.Preparation, watchPath, ct,
                cause: TimelineActors.Human(actor),
                reason: "Decision reopened");
            if (move.Status == MoveJobStatus.Success) targetState = TaskStates.Preparation;
        }

        _logger.LogInformation("decision-reopened job={JobId} lane={Lane}", jobId, targetState);
        return new(DecisionCardStatus.Success, reopened, targetState);
    }

    /// <summary>
    /// Enriches the prompt of every implementation card that depends on this
    /// decision with the decision block and promotes eligible ones to 2-ready.
    /// When nothing depends on it yet and the chosen option carries
    /// requirements, one 2-ready coding card is seeded from those requirements.
    /// </summary>
    private async Task<(List<string> Unblocked, List<string> Created)> UnblockDependantsAsync(
        TaskInfo decisionCard, DecisionContent content, DecisionOption? chosen, string rationale,
        string decidedBy, string? watchPath, CancellationToken ct)
    {
        var unblocked = new List<string>();
        var created = new List<string>();

        var block = BuildDecisionBlock(decisionCard, content, chosen, rationale);

        var dependents = string.IsNullOrWhiteSpace(decisionCard.Key)
            ? Array.Empty<TaskReferenceLink>()
            : _scanner.GetReferenceIndex().Dependents(decisionCard.Key, TaskReferenceKinds.DependsOn);

        foreach (var link in dependents)
        {
            var dependent = _scanner.FindJob(link.SourceJobId, link.SourceWatchPath);
            if (dependent == null) continue;
            AppendToPrompt(dependent.FolderPath, block);
            // Promote a card still waiting in an intake lane to 2-ready; a card
            // already further along keeps its lane.
            if (dependent.State is TaskStates.Backlog or TaskStates.Preparation)
            {
                var move = await _transitions.MoveAsync(
                    link.SourceJobId, TaskStates.Ready, link.SourceWatchPath, ct,
                    cause: TimelineActors.Human(decidedBy),
                    reason: $"Unblocked by decision {decisionCard.Key}");
                if (move.Status != MoveJobStatus.Success) continue;
            }
            if (!string.IsNullOrWhiteSpace(dependent.Key)) unblocked.Add(dependent.Key!);
        }

        // No implementation card yet: seed one from the chosen option's
        // requirements so the decision produces the next concrete piece of work.
        if (dependents.Count == 0 && chosen != null && !string.IsNullOrWhiteSpace(chosen.Requirements))
        {
            var prompt = new StringBuilder();
            prompt.Append(chosen.Requirements!.Trim());
            prompt.Append("\n\n");
            prompt.Append(block);
            var createReq = new CreateTaskRequest
            {
                Title = $"Implement: {chosen.Label}",
                WatchPath = decisionCard.WatchPath,
                PromptMarkdown = prompt.ToString(),
                TargetState = TaskStates.Ready,
                Mode = TaskModes.Coding,
                CreationSource = "system",
                CreatedBy = decidedBy,
                OwnerClientId = decisionCard.OwnerClientId,
            };
            var newJobId = _mutations.CreateJob(createReq);
            if (newJobId != null)
            {
                var newCard = _scanner.FindJob(newJobId, decisionCard.WatchPath);
                if (newCard?.Key is { Length: > 0 } newKey)
                {
                    // Point the new card back at the decision as a related edge so
                    // the record is navigable from the implementation.
                    var refs = newCard.References ?? new TaskReferences();
                    var relatedTo = new List<string>(refs.RelatedTo);
                    if (!string.IsNullOrWhiteSpace(decisionCard.Key)) relatedTo.Add(decisionCard.Key!);
                    _mutations.SetTaskReferences(newJobId, refs with { RelatedTo = relatedTo }, decisionCard.WatchPath);
                    created.Add(newKey);
                }
            }
        }

        return (unblocked, created);
    }

    /// <summary>
    /// Builds the decision block appended to an implementation card's prompt: the
    /// question, chosen option, rationale, and a link to the record. Named as a
    /// heading so it reads as a distinct section of the prompt.
    /// </summary>
    private static string BuildDecisionBlock(
        TaskInfo decisionCard, DecisionContent content, DecisionOption? chosen, string rationale)
    {
        var key = string.IsNullOrWhiteSpace(decisionCard.Key) ? decisionCard.Id : decisionCard.Key;
        var sb = new StringBuilder();
        sb.Append("## Decision ").Append(key).Append(" resolved\n\n");
        if (!string.IsNullOrWhiteSpace(content.Question))
            sb.Append("**Question:** ").Append(content.Question.Trim()).Append("\n\n");
        sb.Append("**Chosen option:** ")
            .Append(chosen != null ? $"{chosen.Id} - {chosen.Label}" : content.ChosenOptionId)
            .Append("\n\n");
        sb.Append("**Rationale:** ").Append(rationale).Append("\n\n");
        sb.Append("**Record:** ").Append(RecordFileName).Append('\n');
        return sb.ToString();
    }

    private void AppendToPrompt(string folderPath, string block)
    {
        try
        {
            var path = Path.Combine(folderPath, "prompt.md");
            var separator = File.Exists(path) ? "\n\n" : "";
            File.AppendAllText(path, separator + block, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "decision-block append failed for {Folder}", folderPath);
        }
    }

    /// <summary>
    /// Writes the ADR-style decision record into the card folder. The record is a
    /// top-level Markdown document surfaced in the Files tab and linked from the
    /// decision content, mirroring the Dossier decision record's fields
    /// (question, options with consequences, recommendation, chosen option,
    /// decider, rationale, timestamp).
    /// </summary>
    private string WriteDecisionRecord(
        TaskInfo card, DecisionContent content, DecisionOption? chosen, string rationale,
        string decidedBy, DateTime nowUtc)
    {
        var key = string.IsNullOrWhiteSpace(card.Key) ? card.Id : card.Key;
        var sb = new StringBuilder();
        sb.Append("# Decision ").Append(key).Append(" - ").Append(card.Title).Append("\n\n");
        sb.Append("- **Status.** Decided\n");
        sb.Append("- **Decider.** ").Append(string.IsNullOrWhiteSpace(decidedBy) ? content.Decider : decidedBy).Append('\n');
        sb.Append("- **Decided at.** ").Append(nowUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")).Append('\n');
        sb.Append("- **Chosen option.** ")
            .Append(chosen != null ? $"{chosen.Id} - {chosen.Label}" : content.ChosenOptionId).Append("\n\n");

        sb.Append("## Question\n\n").Append(content.Question.Trim()).Append("\n\n");

        sb.Append("## Options\n\n");
        foreach (var option in content.Options ?? [])
        {
            var recommended = !string.IsNullOrWhiteSpace(content.RecommendedOptionId)
                && string.Equals(option.Id?.Trim(), content.RecommendedOptionId!.Trim(), StringComparison.OrdinalIgnoreCase);
            var isChosen = chosen != null && string.Equals(option.Id?.Trim(), chosen.Id?.Trim(), StringComparison.OrdinalIgnoreCase);
            sb.Append("### ").Append(option.Id).Append(" - ").Append(option.Label);
            if (recommended) sb.Append(" (recommended)");
            if (isChosen) sb.Append(" — chosen");
            sb.Append('\n');
            if (!string.IsNullOrWhiteSpace(option.Consequences))
                sb.Append("- Consequences: ").Append(option.Consequences!.Trim()).Append('\n');
            if (!string.IsNullOrWhiteSpace(option.Effort))
                sb.Append("- Effort: ").Append(option.Effort!.Trim()).Append('\n');
            if (!string.IsNullOrWhiteSpace(option.Risks))
                sb.Append("- Risks: ").Append(option.Risks!.Trim()).Append('\n');
            sb.Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(content.RecommendationReason))
            sb.Append("## Recommendation\n\n").Append(content.RecommendationReason!.Trim()).Append("\n\n");

        sb.Append("## Rationale\n\n").Append(rationale).Append('\n');

        try
        {
            var path = Path.Combine(card.FolderPath, RecordFileName);
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "decision-record write failed for {Folder}", card.FolderPath);
        }
        return RecordFileName;
    }
}
