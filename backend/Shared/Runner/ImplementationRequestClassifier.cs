using System.Text.RegularExpressions;

namespace AgentStudio.Shared;

/// <summary>
/// Pure classifier for "does this follow-up prompt ask for a source-code
/// change?" - the AGT-2795 evidence class. A concept or planning card is
/// document-first (<see cref="TaskModes.IsReadOnly"/>); a continue prompt
/// that talks about routes, endpoints, bug fixes, or a numeric count change
/// is asking the agent to touch code the mode forbids, which is exactly what
/// silently reran a concept as an implementation fix.
///
/// <para>
/// Deliberately broad rather than narrow, the opposite bias from
/// <see cref="SteerQuestionClassifier"/>: a missed signal here reproduces the
/// incident (a code-shaped prompt silently runs in a document-only mode),
/// while a false positive only costs one explicit
/// <c>ContinueJobRequest.ModeOverride</c> resend. Kept pure (regex over the
/// prompt text) so it is fully unit-testable and carries no I/O.
/// </para>
/// </summary>
public static class ImplementationRequestClassifier
{
    private static readonly Regex[] Signals =
    [
        // A source file extension named in the prompt.
        new(@"\.(cs|csx|ts|tsx|js|jsx|py|go|rb|java|kt|swift|html|scss|css|json|ya?ml|razor|cshtml)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // Nouns that name a piece of running software rather than a document.
        new(@"\b(endpoint|route|routes|controller|handler|middleware|migration|db\s+schema|unit\s+test|" +
            @"integration\s+test|stack\s+trace|null\s+reference|merge\s+conflict|pull\s+request|" +
            @"compile\s+error|build\s+error|hot ?fix)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // "fix/patch/resolve" paired with a defect word, either order.
        new(@"\b(fix|fixes|fixed|patch|patched|resolve|resolved)\w*\b[\w\s,'""./()-]{0,40}\b(bug|bugs|issue|issues|error|errors|defect|defects|regression|crash)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(bug|bugs|issue|issues|error|errors|defect|defects|regression|crash)\b[\w\s,'""./()-]{0,40}\b(fix|fixes|fixed|patch|patched|resolve|resolved)\w*\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // Direct implementation verbs.
        new(@"\bimplement(ed|ing|ation)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\brefactor\w*\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // A numeric change tied to a code-ish counter, the AGT-2795 shape:
        // "demo route count 81 to 83", "bump the retry limit from 3 to 5".
        new(@"\b(count|limit|routes?|endpoints?|threshold|total|number)\w*\b[\s\S]{0,40}\b\d+\s+to\s+\d+\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b\d+\s+to\s+\d+\b[\s\S]{0,40}\b(count|limit|routes?|endpoints?|threshold|total|number)\w*\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    /// <summary>
    /// True when <paramref name="prompt"/> reads as a request to change source
    /// code rather than a concept- or planning-appropriate follow-up (a
    /// decision answer, a dossier text revision, a scoping question).
    /// </summary>
    public static bool LooksLikeImplementationRequest(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        var text = prompt.Trim();
        foreach (var signal in Signals)
            if (signal.IsMatch(text)) return true;
        return false;
    }
}
