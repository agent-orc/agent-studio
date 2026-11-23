using AgentStudio.Shared;

namespace AgentStudio.Tasks;

/// <summary>
/// Who may carry out an archive override once the move route admitted it
/// (Dossier operator-elevated-session, ES-2): a person, or the operator
/// session whose request an Owner approval admitted. The route checks
/// <c>project.cards.override</c>; this guard keeps the lane write from
/// accepting an automated actor or an operator session without an approval.
/// </summary>
internal static class ArchiveOverrideActorPolicy
{
    public const string Refusal = "Archive override requires a human actor or an approved operator session, and a written reason.";

    public static bool Admits(string? cause)
        => TimelineActors.IsHuman(cause) || IsApprovedOperatorSession(cause);

    /// <summary>True when <paramref name="cause"/> is the operator session of this request and an Owner approval admitted it.</summary>
    public static bool IsApprovedOperatorSession(string? cause)
        => TimelineActors.IsOperatorAgent(cause)
           && OperatorActorScope.Current is { ApprovalId: not null } actor
           && string.Equals(actor.Actor, cause!.Trim(), StringComparison.Ordinal);
}
