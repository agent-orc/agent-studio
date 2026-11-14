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

    /// <summary>
    /// Stable error code of the 409 refusal (AGT-3700). The continue panel
    /// keys on it to offer "keep in this mode and send" and "promote" as
    /// buttons instead of showing the raw message.
    /// </summary>
    public const string RefusalCode = "continue-mode-guard";
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
/// via <see cref="ContinueModeGuardFacts.ModeOverride"/>. A follow-up that
/// names a Dossier section or decision is a Dossier edit and is never refused
/// (AGT-3700): "add one more section D3 File naming" pairs a change verb with
/// a code-shaped noun, but it is exactly the work a concept card exists for.
/// Coding and research
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

    /// <summary>Words that name a part of a Dossier rather than of the code.</summary>
    private static readonly string[] DossierNouns =
        ["dossier", "section", "sections", "subsection", "subsections", "decision", "decisions",
         "chapter", "chapters", "heading", "headings", "appendix", "paragraph", "paragraphs",
         "recommendation", "recommendations", "rationale", "open question", "open questions"];

    private static readonly Regex DossierNounPattern = WordPattern(DossierNouns);

    /// <summary>A Dossier decision id like "D3" or an "Option B" choice; ids are upper case.</summary>
    private static readonly Regex DossierIdPattern =
        new(@"\bD\d{1,2}\b|\b[Oo]ption\s+[A-Z0-9]\b|§\s*\d", RegexOptions.Compiled);

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

        // AGT-3700: the message is shown to the operator as is, so it names the
        // two choices in words; API callers resend with modeOverride=<mode>.
        var message =
            $"This card is in '{mode}' mode and its pipeline is read only, and this follow-up reads like a " +
            $"code-change request. Keep the card in {mode} mode and send the follow-up anyway, or promote " +
            "the card to implementation.";

        return new ContinueModeGuardDecision(ContinueModeGuardAction.Reject, message);
    }

    /// <summary>
    /// A curated, deterministic heuristic - not NLP. It looks for an
    /// imperative code-change verb, a code fence, or a change verb paired
    /// with a code-shaped target noun or a before/after numeric range (the
    /// AGT-2795 shape: "demo route count 81 to 83"). A prompt that names a
    /// Dossier section or decision never matches (AGT-3700).
    /// </summary>
    public static bool LooksLikeImplementationRequest(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        if (NamesDossierPart(prompt)) return false;

        if (CodeFencePattern.IsMatch(prompt)) return true;
        if (StrongVerbPattern.IsMatch(prompt)) return true;
        if (ChangeVerbPattern.IsMatch(prompt) && TargetNounPattern.IsMatch(prompt)) return true;
        if (NumericRangePattern.IsMatch(prompt) && TargetNounPattern.IsMatch(prompt)) return true;

        return false;
    }

    /// <summary>True when the prompt names a Dossier section, decision, or option.</summary>
    public static bool NamesDossierPart(string prompt) =>
        DossierNounPattern.IsMatch(prompt) || DossierIdPattern.IsMatch(prompt);

    private static bool IsExplicitlyConfirmed(string? modeOverride, string currentMode) =>
        !string.IsNullOrWhiteSpace(modeOverride)
        && string.Equals(TaskModes.Normalize(modeOverride), currentMode, StringComparison.Ordinal);

    private static Regex WordPattern(IReadOnlyList<string> words) =>
        new($@"\b(?:{string.Join("|", words.Select(word => Regex.Escape(word).Replace(@"\ ", @"\s+")))})\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
