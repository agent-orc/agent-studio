using AgentStudio.Shared;

namespace AgentStudio.Tasks;

/// <summary>
/// Pure policy for the one invariant a parked card's summary may never break:
/// <b>a card that parked itself has an open item</b>.
///
/// <para>AGT-2736 sat in <c>5e-escalated</c> for three days showing
/// <c>Result: Success</c> and <c>Open Items: None</c> while the run was in fact
/// waiting on an operator decision it could not take. The run's own text was not
/// wrong about what it shipped; the summary was wrong about what was open. This
/// policy is applied where a summary stub is PRODUCED - the escalation stub and
/// the transition result scaffold - and never by rewriting an agent's text.</para>
/// </summary>
public static class ParkedOpenItems
{
    public const string Heading = "## Open Items";

    /// <summary>Stated when the parking run left neither a question nor a reason.</summary>
    public const string UnstatedQuestion = "the parking run recorded no question";

    /// <summary>
    /// The park expressed as checklist items. Never empty: the first item is the
    /// park itself, so no caller can render "None" for a parked card.
    /// </summary>
    /// <param name="blockerType">Escalation category, or <c>operator-decision</c>.</param>
    /// <param name="question">The one-sentence question, when the run stated one.</param>
    /// <param name="reason">The freetext park reason, used when no question exists.</param>
    /// <param name="conditionDescription">What must become true for the park to clear.</param>
    public static IReadOnlyList<string> Items(
        string? blockerType,
        string? question,
        string? reason,
        string? conditionDescription = null)
    {
        var type = Text(blockerType) ?? ParkedBlockerCatalog.OperatorDecision;
        var statement = Text(question) ?? Text(reason) ?? UnstatedQuestion;
        var items = new List<string>
        {
            $"This card is parked ({type}) and waits for a person: {statement}",
        };
        var condition = Text(conditionDescription);
        if (condition is not null) items.Add($"Clears when: {condition}");
        return items;
    }

    /// <summary>Convenience overload for a projected park.</summary>
    public static IReadOnlyList<string> Items(ParkedBlockerStatus park)
        => Items(park.BlockerType, park.Decision?.Question, park.Reason, park.ConditionDescription);

    /// <summary>
    /// The full <c>## Open Items</c> section for a parked card, as unchecked
    /// checklist rows so the completion gate reads them as genuinely open.
    /// </summary>
    public static string Section(
        string? blockerType,
        string? question,
        string? reason,
        string? conditionDescription = null)
    {
        var nl = Environment.NewLine;
        var sb = new System.Text.StringBuilder();
        sb.Append(Heading).Append(nl).Append(nl);
        foreach (var item in Items(blockerType, question, reason, conditionDescription))
            sb.Append("- [ ] ").Append(SingleLine(item)).Append(nl);
        return sb.ToString();
    }

    private static string? Text(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>Checklist rows are one line; a wrapped reason would break the
    /// section into stray body text.</summary>
    private static string SingleLine(string value)
        => value.Replace("\r", " ").Replace("\n", " ").Replace("  ", " ").Trim();
}
