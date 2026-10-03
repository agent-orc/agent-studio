namespace AgentStudio.Tasks;

/// <summary>What the apply step did for one recorded choice.</summary>
public sealed record DecisionApplyResult(
    string Outcome,
    IReadOnlyList<string> TaskKeys,
    IReadOnlyList<string> Notes);

/// <summary>
/// Card binding of the apply step (Dossier decision-cards D3=C). A linked
/// implementation card receives the decision block and moves to
/// <c>2-ready</c>; with no linked card the chosen option's requirements become
/// cards through the concept promotion mechanism. The decision stays recorded
/// when apply fails; the failure is reported, not rolled back.
/// </summary>
public sealed class DecisionApplyService
{
    private const string ReasonPrefix = "decision-apply:";

    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly TaskTransitionService _transitions;
    private readonly AgentStudio.Pipeline.ConceptPromotionService _promotion;
    private readonly ILogger<DecisionApplyService> _logger;

    public DecisionApplyService(TaskScannerService scanner, TaskMutationService mutations,
        TaskTransitionService transitions, AgentStudio.Pipeline.ConceptPromotionService promotion,
        ILogger<DecisionApplyService> logger)
    {
        _scanner = scanner;
        _mutations = mutations;
        _transitions = transitions;
        _promotion = promotion;
        _logger = logger;
    }

    public async Task<DecisionApplyResult> ApplyAsync(TaskInfo decisionCard, DecisionContent decided,
        CancellationToken ct = default)
    {
        var key = decisionCard.Key ?? decisionCard.Id;
        var linked = (decided.AppliesTo ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Where(value => !string.Equals(value, key, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(value => (Key: value, Card: _scanner.FindJob(value, decisionCard.WatchPath)))
            .ToList();
        var plan = DecisionApplyPolicy.Plan(decided,
            linked.Select(item => new DecisionLinkedCardFact(item.Key, item.Card?.State)).ToList());
        var block = DecisionPromptBlock.Render(key, decisionCard.Title, decided);
        var marker = DecisionPromptBlock.Marker(key, decided);

        return plan.Outcome switch
        {
            DecisionApplyOutcomes.LinkedCards => await ApplyToLinkedAsync(
                key, decided, plan, linked.ToDictionary(item => item.Key, item => item.Card,
                    StringComparer.OrdinalIgnoreCase), block, marker, ct),
            DecisionApplyOutcomes.CreatedCards => CreateFromRequirements(decisionCard, key, decided, plan, block),
            _ => new(DecisionApplyOutcomes.Nothing, [],
                ["No implementation card is linked and the chosen option names no requirements."]),
        };
    }

    private async Task<DecisionApplyResult> ApplyToLinkedAsync(string key, DecisionContent decided,
        DecisionApplyPlan plan, IReadOnlyDictionary<string, TaskInfo?> cards, string block, string marker,
        CancellationToken ct)
    {
        var applied = new List<string>();
        var notes = new List<string>();
        foreach (var step in plan.LinkedCards)
        {
            var card = cards[step.Key];
            if (step.Action is DecisionLinkedCardAction.Missing or DecisionLinkedCardAction.Skip || card is null)
            {
                notes.Add($"{step.Key}: {step.Reason}");
                continue;
            }
            var promptPath = Path.Combine(card.FolderPath, "prompt.md");
            var prompt = File.Exists(promptPath) ? File.ReadAllText(promptPath) : string.Empty;
            if (!_mutations.UpdateJobFile(card.Id, "prompt.md",
                    DecisionPromptBlock.Append(prompt, block, marker, key), card.WatchPath))
            {
                notes.Add($"{step.Key}: the decision block could not be written.");
                continue;
            }
            applied.Add(card.Key ?? card.Id);
            if (step.Action != DecisionLinkedCardAction.AppendAndMove) continue;

            var move = await _transitions.MoveAsync(card.Id, TaskStates.Ready, card.WatchPath, ct,
                cause: TimelineActors.Human(decided.DecidedBy ?? DecisionDeciders.Operator),
                reason: $"Decision {key} applied");
            if (move.Status != MoveJobStatus.Success)
                notes.Add($"{step.Key}: decision block added, but the move to {TaskStates.Ready} was refused: {move.Message}");
        }
        _logger.LogInformation("decision-applied decision={Key} outcome=linked cards={Cards}",
            key, string.Join(",", applied));
        return new(applied.Count == 0 ? DecisionApplyOutcomes.Failed : DecisionApplyOutcomes.LinkedCards,
            applied, notes);
    }

    private DecisionApplyResult CreateFromRequirements(TaskInfo decisionCard, string key,
        DecisionContent decided, DecisionApplyPlan plan, string block)
    {
        var cycle = $"{(decided.DecidedAt ?? DateTime.UtcNow).ToUniversalTime().Ticks}:{decided.History.Count}";
        var spawns = plan.Requirements.Select((requirement, index) =>
            new AgentStudio.Pipeline.ConceptCardSpawn(
                $"{ReasonPrefix}{key}:{cycle}:{index}",
                new CreateTaskRequest
                {
                    Title = requirement.Title.Trim(),
                    PromptMarkdown = block + "\n" + requirement.PromptMarkdown.Trim() + "\n",
                    AcceptanceScope = AgentStudio.Pipeline.DossierImplementationCardPolicy.AcceptanceScopeFor(requirement),
                    Mode = TaskModes.Coding,
                    TargetState = TaskStates.Ready,
                })).ToList();
        var created = _promotion.CreateCards(decisionCard, spawns);
        var keys = created.Select(card => card.TaskKey ?? card.JobId).ToList();
        _logger.LogInformation("decision-applied decision={Key} outcome=created cards={Cards}",
            key, string.Join(",", keys));
        return new(DecisionApplyOutcomes.CreatedCards, keys, []);
    }
}
