using System.Text.RegularExpressions;

namespace AgentRunner;

/// <summary>Allows completed deliveries to enter review when only review approval remains.</summary>
public static class ApprovalOnlyNeedsInputPolicy
{
    private static readonly Regex Approval = new(
        @"\b(approv(?:al|e|ed)|accept(?:ance)?|sight[ -]?review|human review|operator review|review (?:and |of |the |required|needed|pending)|decision(?:s)? (?:D\d|acceptance|approval|confirmation)|(?:scope|decision) confirmation)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MissingFact = new(
        @"\b(missing|absent|unavailable|cannot (?:find|obtain|access)|can't (?:find|obtain|access)|credential(?:s)?|secret(?:s)?|token|which (?:file|path|column|scope|option)|ambiguous scope|no recommendation|no production gate host|blocked by|cannot proceed|can't proceed|no files were changed|stopped before changing code|implementation is waiting|waiting on the operator|please (?:choose|select|confirm))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static RunOutcome Classify(RunOutcome outcome)
    {
        if (outcome.Kind != RunOutcomeKind.NeedsInput) return outcome;
        var text = string.Join("\n", outcome.NeedsInputMessage, outcome.Reason);
        return Approval.IsMatch(text) && !MissingFact.IsMatch(text)
            ? new RunOutcome(RunOutcomeKind.Done, "review-requested: " + outcome.Reason)
            : outcome;
    }
}
