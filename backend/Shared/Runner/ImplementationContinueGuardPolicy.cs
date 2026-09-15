namespace AgentStudio.Shared;

/// <summary>What <see cref="ImplementationContinueGuardPolicy.Decide"/> concluded.</summary>
public enum ImplementationContinueAction
{
    /// <summary>The continue may proceed to the normal admission/spawn path.</summary>
    Allow,

    /// <summary>The continue must be refused with <c>409</c> before any side effect.</summary>
    Reject,
}

/// <summary>
/// <paramref name="Reason"/> is non-null exactly when <paramref name="Action"/>
/// is <see cref="ImplementationContinueAction.Reject"/>.
/// </summary>
public sealed record ImplementationContinueDecision(ImplementationContinueAction Action, string? Reason);

/// <summary>
/// Guards <c>POST /api/tasks/{id}/continue</c> against the AGT-2795 failure
/// mode: a card repurposed into a concept or planning dossier received an
/// implementation-fix prompt, ran it in document-only mode, produced no code
/// change, and silently overwrote the card's Result with an escalation
/// summary instead of refusing the request.
///
/// <para>
/// A card whose mode is <see cref="TaskModes.Concept"/> or
/// <see cref="TaskModes.Planning"/> rejects a prompt that
/// <see cref="ImplementationRequestClassifier"/> reads as a code-change
/// request, naming the mode and the matching promotion route
/// (<c>promote-concept</c> for concept, <c>promote-to-coding</c> for
/// planning) so the operator can spawn a proper coding card instead. Research
/// mode is also read-only (<see cref="TaskModes.IsReadOnly"/>) but is not
/// guarded here: it has no bounded "one dossier" contract to protect and no
/// promotion route to point to, so the card's blast radius is different.
/// </para>
///
/// <para>
/// <c>modeOverride</c> is an explicit, one-shot operator decision - modeled
/// on <c>MoveJobRequest.OperatorOverride</c> - and always wins: the server
/// never infers it from the prompt text.
/// </para>
/// </summary>
public static class ImplementationContinueGuardPolicy
{
    private static readonly IReadOnlySet<string> GuardedModes =
        new HashSet<string>(StringComparer.Ordinal) { TaskModes.Concept, TaskModes.Planning };

    private static readonly ImplementationContinueDecision AllowDecision =
        new(ImplementationContinueAction.Allow, null);

    public static ImplementationContinueDecision Decide(string? cardMode, string? prompt, bool modeOverride)
    {
        if (modeOverride) return AllowDecision;

        var mode = TaskModes.Normalize(cardMode);
        if (!GuardedModes.Contains(mode)) return AllowDecision;
        if (!ImplementationRequestClassifier.LooksLikeImplementationRequest(prompt)) return AllowDecision;

        var promotePath = mode == TaskModes.Concept ? "promote-concept" : "promote-to-coding";
        var reason =
            $"This card is in '{mode}' mode and does not run implementation prompts. " +
            $"Promote it through POST /api/tasks/{{id}}/{promotePath} to create a coding card, " +
            "or resend this continue with modeOverride: true to run it on the card as-is.";
        return new ImplementationContinueDecision(ImplementationContinueAction.Reject, reason);
    }
}
