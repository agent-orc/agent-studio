namespace AgentStudio.Shared;

/// <summary>
/// The operator agent session a request runs for (security model SM-1): the
/// operator principal, the child session, and the Owner who started that
/// session. <see cref="Actor"/> is the one actor string every request, audit
/// entry, and timeline event of the session carries; <see cref="DelegatedBy"/>
/// names the delegating Owner, who is never the actor.
/// </summary>
public sealed record OperatorActor(string OperatorAgentId, string SessionId, string OwnerId)
{
    public string Actor => TimelineActors.OperatorAgent(OperatorAgentId, SessionId);
    public string DelegatedBy => TimelineActors.Human(OwnerId);
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
    /// delegating Owner. A non-human actor a handler passed (system, agent,
    /// orchestrator) stays in <c>details.reportedActor</c>; a human actor is
    /// dropped, because a session never acts as a person.
    /// </summary>
    public static TimelineEvent Attribute(TimelineEvent evt)
    {
        if (Current is not { } actor) return evt;
        var details = evt.Details is null ? null : new Dictionary<string, string>(evt.Details);
        if (!string.IsNullOrWhiteSpace(evt.Actor) && evt.Actor != actor.Actor && !TimelineActors.IsHuman(evt.Actor)
            && !TimelineActors.IsOperatorAgent(evt.Actor))
            (details ??= [])["reportedActor"] = evt.Actor;
        return evt with { Actor = actor.Actor, DelegatedBy = actor.DelegatedBy, Details = details };
    }

    private sealed class Restore(OperatorActor? previous) : IDisposable
    {
        public void Dispose() => CurrentActor.Value = previous;
    }
}
