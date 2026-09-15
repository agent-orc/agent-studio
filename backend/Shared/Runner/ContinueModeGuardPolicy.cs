using System.Text.RegularExpressions;

namespace AgentStudio.Shared;

/// <summary>
/// What <see cref="ContinueModeGuardPolicy.Decide"/> resolved for one
/// <c>continue</c> request against the card's current <see cref="TaskModes"/>.
/// </summary>
public enum ContinueModeGuardAction
{
    /// <summary>The continue may proceed.</summary>
    Allow,

    /// <summary>The continue must be refused with the paired message.</summary>
    Reject,
}

/// <summary>
/// Plain facts the guard needs. <paramref name="ModeOverride"/> is the raw
/// value the caller sent on <see cref="ContinueJobRequest.ModeOverride"/>;
/// it is not the same axis as <see cref="ContinueModes"/> (conversational
/// framing) - it names a <see cref="TaskModes"/> value the caller explicitly
/// confirms they mean to keep running under.
/// </summary>
public sealed record ContinueModeGuardFacts(string JobId, string? Mode, string Prompt, string? ModeOverride);

/// <summary>
/// Outcome of <see cref="ContinueModeGuardPolicy.Decide"/>.
/// <paramref name="Message"/> is null exactly when <paramref name="Action"/>
/// is <see cref="ContinueModeGuardAction.Allow"/>.
/// </summary>
public sealed record ContinueModeGuardDecision(ContinueModeGuardAction Action, string? Message)
{
    public static readonly ContinueModeGuardDecision Allowed = new(ContinueModeGuardAction.Allow, null);
}

/// <summary>
/// Stops a <c>continue</c> from silently re-running a concept or planning
/// card with an implementation-shaped prompt (AGT-2795): the card's pipeline
/// is read-only there, so the run does no implementation, writes a fresh
/// <c>status.md</c> over the prior one, and escalates for decisions nobody
/// asked for. The card's read-only pipeline scope (see
/// <c>docs/system/domains/pipeline.md</c>) already prevents an actual code
/// diff from landing; this guard exists to stop the wasted, confusing run
/// before it starts, not to enforce the diff boundary a second time.
///
/// <para>
/// A concept or planning card whose follow-up prompt reads as a code-change
/// request is refused with a message naming the mode and the matching
/// promotion path, unless the caller explicitly confirms the current mode
/// via <see cref="ContinueModeGuardFacts.ModeOverride"/>. Coding and research
/// cards are never guarded: research is read-only but has no promotion path
/// this guard can name, and a stray "fix" in a research follow-up is not the
/// failure this policy exists to catch.
/// </para>
/// </summary>
public static class ContinueModeGuardPolicy
{
    /// <summary>Imperative verbs that alone are strong evidence of a code-change request.</summary>
    private static readonly string[] StrongVerbs =
        ["fix", "implement", "patch", "refactor", "rewrite", "debug"];

    /// <summary>Change verbs that only signal a code change alongside a code-shaped target noun.</summary>
    private static readonly string[] ChangeVerbs =
        ["add", "update", "change", "remove", "delete", "bump", "increase", "decrease",
         "modify", "adjust", "replace", "rename", "revert", "migrate", "correct", "resolve"];

    private static readonly string[] TargetNouns =
        ["code", "route", "routes", "endpoint", "endpoints", "function", "functions",
         "method", "methods", "class", "classes", "component", "components", "file", "files",
         "bug", "bugs", "test", "tests", "api", "feature", "count", "config", "configuration",
         "schema", "query", "script", "module", "property", "field", "param", "parameter",
         "flag", "constant", "variable"];

    private static readonly Regex StrongVerbPattern = WordPattern(StrongVerbs);
    private static readonly Regex ChangeVerbPattern = WordPattern(ChangeVerbs);
    private static readonly Regex TargetNounPattern = WordPattern(TargetNouns);

    /// <summary>A before/after change like "route count 81 to 83" or "timeout 30 -> 60".</summary>
    private static readonly Regex NumericRangePattern =
        new(@"\b\d+\s*(?:to|->|→)\s*\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CodeFencePattern = new("```", RegexOptions.Compiled);

    public static ContinueModeGuardDecision Decide(ContinueModeGuardFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var mode = TaskModes.Normalize(facts.Mode);
        if (mode != TaskModes.Concept && mode != TaskModes.Planning)
            return ContinueModeGuardDecision.Allowed;

        if (IsExplicitlyConfirmed(facts.ModeOverride, mode))
            return ContinueModeGuardDecision.Allowed;

        if (!LooksLikeImplementationRequest(facts.Prompt))
            return ContinueModeGuardDecision.Allowed;

        var promotePath = mode == TaskModes.Concept
            ? $"/api/tasks/{facts.JobId}/promote-concept"
            : $"/api/tasks/{facts.JobId}/promote-to-coding";

        var message =
            $"This card is in '{mode}' mode and its pipeline is read-only; this follow-up reads like a " +
            $"code-change request. Promote it to an implementation card first via {promotePath}, or resend " +
            $"the continue with modeOverride=\"{mode}\" to confirm it should stay in {mode} mode.";

        return new ContinueModeGuardDecision(ContinueModeGuardAction.Reject, message);
    }

    /// <summary>
    /// A curated, deterministic heuristic - not NLP. It looks for an
    /// imperative code-change verb, a code fence, or a change verb paired
    /// with a code-shaped target noun or a before/after numeric range (the
    /// AGT-2795 shape: "demo route count 81 to 83").
    /// </summary>
    public static bool LooksLikeImplementationRequest(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;

        if (CodeFencePattern.IsMatch(prompt)) return true;
        if (StrongVerbPattern.IsMatch(prompt)) return true;
        if (ChangeVerbPattern.IsMatch(prompt) && TargetNounPattern.IsMatch(prompt)) return true;
        if (NumericRangePattern.IsMatch(prompt) && TargetNounPattern.IsMatch(prompt)) return true;

        return false;
    }

    private static bool IsExplicitlyConfirmed(string? modeOverride, string currentMode) =>
        !string.IsNullOrWhiteSpace(modeOverride)
        && string.Equals(TaskModes.Normalize(modeOverride), currentMode, StringComparison.Ordinal);

    private static Regex WordPattern(IReadOnlyList<string> words) =>
        new($@"\b(?:{string.Join("|", words.Select(Regex.Escape))})\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
