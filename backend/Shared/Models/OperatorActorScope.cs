namespace AgentStudio.Shared;

/// <summary>
/// The operator agent session a request runs for (security model SM-1): the
/// operator principal, the child session, and the Owner who started that
/// session. <see cref="Actor"/> is the one actor string every request, audit
/// entry, and timeline event of the session carries; <see cref="DelegatedBy"/>
/// names the delegating Owner, who is never the actor.
/// </summary>
public sealed record OperatorActor(string OperatorAgentId, string SessionId, string OwnerId, string? PersonaId = null)
{
    public string Actor => PersonaId is null ? TimelineActors.OperatorAgent(OperatorAgentId, SessionId)
        : TimelineActors.Persona(PersonaId, SessionId);
    public string DelegatedBy => TimelineActors.Human(OwnerId);

    /// <summary>
    /// The Owner approval this request runs under (SM-5), or <c>null</c>.
    /// The access middleware sets it once the approval is redeemed, so every
    /// event of the request names the approval and the approving Owner (ES-2).
    /// </summary>
    public string? ApprovalId { get; init; }

    /// <summary>The user id of the Owner who approved <see cref="ApprovalId"/>.</summary>
    public string? ApprovedBy { get; init; }
    public string? BatchId { get; init; }
}

/// <summary>
/// Ambient operator attribution for the current request. The access middleware
/// opens it for an operator session token; the timeline writer and the control
/// plane audit writer read it, so an event a handler writes during that request
/// is attributed to the session even when the handler passes another actor.
/// </summary>
public static class OperatorActorScope
{
    private static readonly AsyncLocal<OperatorActor?> CurrentActor = new();

    public static OperatorActor? Current => CurrentActor.Value;

    public static IDisposable Enter(OperatorActor actor)
    {
        var previous = CurrentActor.Value;
        CurrentActor.Value = actor;
        return new Restore(previous);
    }

    /// <summary>
    /// The event as the operator session wrote it: the session's actor and
    /// delegating Owner, and for a request an Owner approval admitted the
    /// approval id and the approving Owner (<c>details.approvalId</c>,
    /// <c>details.approvedBy</c>). A non-human actor a handler passed (system, agent,
    /// orchestrator) stays in <c>details.reportedActor</c>; a human actor is
    /// dropped, because a session never acts as a person.
    /// </summary>
    public static TimelineEvent Attribute(TimelineEvent evt)
    {
        if (Current is not { } actor) return evt;
        var details = evt.Details is null ? null : new Dictionary<string, string>(evt.Details);
        if (!string.IsNullOrWhiteSpace(evt.Actor) && evt.Actor != actor.Actor && !TimelineActors.IsHuman(evt.Actor)
            && !TimelineActors.IsOperatorAgent(evt.Actor) && !TimelineActors.IsPersona(evt.Actor))
            (details ??= [])["reportedActor"] = evt.Actor;
        if (actor.BatchId is { } batchId) (details ??= [])["batchId"] = batchId;
        if (actor.ApprovalId is { } approvalId)
        {
            (details ??= [])["approvalId"] = approvalId;
            if (actor.ApprovedBy is { } approvedBy) details["approvedBy"] = TimelineActors.Human(approvedBy);
        }
        return evt with { Actor = actor.Actor, DelegatedBy = actor.DelegatedBy, Details = details };
    }

    private sealed class Restore(OperatorActor? previous) : IDisposable
    {
        public void Dispose() => CurrentActor.Value = previous;
    }
}
