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
        return AgentStudio.Orchestration.Decisions.TransitionDecisionPolicy.Salvage(
            outcome, hasSalvageCommit, automaticContinuationRoundsUsed) switch
        {
            AgentStudio.Orchestration.Decisions.TransitionAction.Continue => RunTimeoutSalvageAction.None,
            AgentStudio.Orchestration.Decisions.TransitionAction.SalvageContinuation => RunTimeoutSalvageAction.StartContinuation,
            _ => RunTimeoutSalvageAction.Escalate,
        };
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

