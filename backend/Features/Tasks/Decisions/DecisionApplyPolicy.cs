using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Tasks;

/// <summary>What the apply step does with one linked implementation card.</summary>
public enum DecisionLinkedCardAction
{
    /// <summary>Append the decision block and move the card to <c>2-ready</c>.</summary>
    AppendAndMove,
    /// <summary>Append the decision block; the card already waits in <c>2-ready</c>.</summary>
    AppendOnly,
    /// <summary>Leave the card untouched; it is already past the decision point.</summary>
    Skip,
    /// <summary>The linked key does not resolve to a card.</summary>
    Missing,
}

/// <summary>One linked card as the apply policy sees it; <see cref="State"/> is null when unresolved.</summary>
public sealed record DecisionLinkedCardFact(string Key, string? State);

/// <summary>The planned action for one linked card, with the reason shown in the record.</summary>
public sealed record DecisionLinkedCardStep(string Key, DecisionLinkedCardAction Action, string? Reason = null);

/// <summary>
/// The apply plan for a recorded choice (Dossier decision-cards D3=C): either
/// the linked implementation cards are updated, or cards are created from the
/// chosen option's requirements, or there is nothing to apply.
/// </summary>
public sealed record DecisionApplyPlan(
    string Outcome,
    IReadOnlyList<DecisionLinkedCardStep> LinkedCards,
    IReadOnlyList<ConceptImplementationTask> Requirements);

/// <summary>
/// Pure apply policy for a decided decision card. The card binding reads the
/// linked cards' lanes and applies the plan; this type only chooses.
/// </summary>
public static class DecisionApplyPolicy
{
    /// <summary>Lanes a linked card waits in while its decision is open; apply moves it on to <c>2-ready</c>.</summary>
    private static readonly HashSet<string> WaitingLanes = new(StringComparer.Ordinal)
    {
        TaskStates.Backlog, TaskStates.Preparation, TaskStates.OrchestratorPrep, TaskStates.Escalated,
    };

    /// <param name="linked">The linked implementation cards, without blank keys and without the
    /// decision's own key; an <c>appliesTo</c> that names only the decision links nothing.</param>
    public static DecisionApplyPlan Plan(DecisionContent decided, IReadOnlyList<DecisionLinkedCardFact> linked)
    {
        if (linked.Count > 0)
            return new(DecisionApplyOutcomes.LinkedCards, linked.Select(Step).ToList(), []);

        var requirements = DecisionCardPolicy.ChosenOption(decided)?.Requirements ?? [];
        return requirements.Count == 0
            ? new(DecisionApplyOutcomes.Nothing, [], [])
            : new(DecisionApplyOutcomes.CreatedCards, [], requirements);
    }

    private static DecisionLinkedCardStep Step(DecisionLinkedCardFact card)
    {
        if (card.State is null)
            return new(card.Key, DecisionLinkedCardAction.Missing, "The linked card does not exist.");
        if (WaitingLanes.Contains(card.State))
            return new(card.Key, DecisionLinkedCardAction.AppendAndMove);
        if (card.State == TaskStates.Ready)
            return new(card.Key, DecisionLinkedCardAction.AppendOnly);
        return new(card.Key, DecisionLinkedCardAction.Skip,
            $"The linked card is already in {card.State}; its prompt was left unchanged.");
    }
}

/// <summary>
/// Renders the decision block the apply step writes into an implementation
/// card's prompt: question, chosen option, rationale, and the record link. The
/// marker keeps the append idempotent when apply runs again for the same choice.
/// </summary>
public static class DecisionPromptBlock
{
    private const string FinalInstruction = "Implement the chosen option. The decision is settled; do not reopen it in this run.";

    // The history count distinguishes two choices even if the clock gives them
    // the same timestamp. Every recorded choice appends a history entry.
    public static string Marker(string decisionKey, DecisionContent decided) =>
        $"<!-- agent-studio:decision-apply {decisionKey} {(decided.DecidedAt ?? DateTime.UtcNow).ToUniversalTime().Ticks}:{decided.History.Count} -->";

    public static string Render(string decisionKey, string title, DecisionContent decided)
    {
        var chosen = DecisionCardPolicy.ChosenOption(decided);
        var decidedAt = decided.DecidedAt ?? DateTime.UtcNow;
        var sb = new StringBuilder();
        sb.Append("## Decision ").Append(decisionKey).Append(": ").Append(title.Trim()).Append("\n\n");
        sb.Append(Marker(decisionKey, decided)).Append("\n\n");
        sb.Append("- Question: ").Append(decided.Question.Trim()).Append('\n');
        sb.Append("- Chosen option: ").Append(chosen?.Id ?? decided.ChosenOptionId)
            .Append(" · ").Append(chosen?.Label ?? "").Append('\n');
        if (!string.IsNullOrWhiteSpace(chosen?.Consequences))
            sb.Append("- Consequences: ").Append(chosen.Consequences.Trim()).Append('\n');
        sb.Append("- Rationale: ")
            .Append(string.IsNullOrWhiteSpace(decided.Rationale) ? "none given" : decided.Rationale.Trim()).Append('\n');
        sb.Append("- Decided by: ").Append(decided.DecidedBy ?? DecisionDeciders.Operator)
            .Append(" at ").Append(decidedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")).Append('\n');
        sb.Append("- Record: ")
            .Append(string.IsNullOrWhiteSpace(decided.RecordPath)
                ? $"decision card {decisionKey}"
                : $"project wiki `{decided.RecordPath}` (decision card {decisionKey})")
            .Append('\n');
        sb.Append("\n").Append(FinalInstruction).Append('\n');
        return sb.ToString();
    }

    /// <summary>Replaces an earlier choice for this decision, or appends the first one.</summary>
    public static string Append(string? prompt, string block, string marker, string decisionKey)
    {
        var current = prompt ?? string.Empty;
        if (current.Contains(marker, StringComparison.Ordinal)) return current;
        var oldBlock = $"^## Decision {Regex.Escape(decisionKey)}:[^\r\n]*\r?\n\r?\n"
            + $"<!-- agent-studio:decision-apply {Regex.Escape(decisionKey)} [^\r\n]* -->\r?\n\r?\n"
            + $".*?^{Regex.Escape(FinalInstruction)}\r?\n?";
        var trimmed = Regex.Replace(current, oldBlock, string.Empty,
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant).TrimEnd();
        return trimmed.Length == 0 ? block : trimmed + "\n\n" + block;
    }
}
