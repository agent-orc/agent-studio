namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// One member a batch claimed. <paramref name="ReviewAttemptId"/> is the
/// member's immutable review attempt, the key of its disposition; it is null
/// only for a manifest closed without a queued review (a test fixture or a
/// record migrated from a batch whose pending record is gone).
/// </summary>
public sealed record BatchMemberClaim(
    string TaskKey, string RunAttempt, string? ReviewAttemptId,
    BatchMemberDisposition Disposition);

/// <summary>
/// Facts of one requested phase change. <paramref name="Current"/> is null
/// while the batch has no phase record yet (a closed manifest without its first
/// state). <paramref name="ExpectedVersion"/> is the compare value of a caller
/// that decided on a version it read; null asks only for a legal successor.
/// <paramref name="ReplayOutcomes"/> maps a member's task key to its replay
/// outcome (<c>admitted</c>, <c>conflict</c>, <c>unattributable</c> or
/// <c>cascade-deferred</c>); the host reads it for terminal phases only.
/// </summary>
public sealed record BatchPhaseTransitionFacts(
    BatchPhase? Current, long CurrentVersion, long CurrentFence,
    BatchPhase Requested, long RequestedFence, long? ExpectedVersion,
    IReadOnlyList<BatchMemberClaim> Members,
    IReadOnlyDictionary<string, string> ReplayOutcomes);

/// <summary>
/// An accepted change carries the next <paramref name="Version"/>, which is
/// also the number of the state history entry, and the member dispositions
/// after the change. A refused change names its <paramref name="Refusal"/>.
/// </summary>
public sealed record BatchPhaseTransitionDecision(
    bool Accepted, string? Refusal, long Version,
    IReadOnlyList<BatchMemberClaim> Members);

/// <summary>
/// AGT-3563: the legal phase changes of one batch and what each change does to
/// the members the batch claimed. Every phase change of the batch gate is a
/// compare-and-swap of the batch phase record through this rule: the requested
/// phase must be a legal successor, the coordinator fence may not go back, and
/// a caller that read a version must still see it.
/// </summary>
public static class BatchPhaseTransitionPolicy
{
    public const string IllegalTransition = "illegal-transition";
    public const string StaleFence = "stale-fence";
    public const string StaleVersion = "stale-version";

    private static readonly IReadOnlyDictionary<BatchPhase, BatchPhase[]> Successors =
        new Dictionary<BatchPhase, BatchPhase[]>
        {
            // A formed batch can be annotated (a repack reason), start
            // assembly, pause on an assembly failure or be retired.
            [BatchPhase.Formed] = [BatchPhase.Formed, BatchPhase.Assembling, BatchPhase.Paused, BatchPhase.Abandoned],
            [BatchPhase.Assembling] = [BatchPhase.Assembling, BatchPhase.Assembled, BatchPhase.Paused, BatchPhase.Abandoned],
            [BatchPhase.Assembled] = [BatchPhase.Running, BatchPhase.Paused, BatchPhase.Abandoned],
            [BatchPhase.Running] = [BatchPhase.Green, BatchPhase.Red, BatchPhase.Paused, BatchPhase.Abandoned],
            [BatchPhase.Green] = [BatchPhase.Publishing, BatchPhase.Paused, BatchPhase.Abandoned],
            [BatchPhase.Publishing] = [BatchPhase.Published, BatchPhase.Paused, BatchPhase.Abandoned],
            // A red parent pauses its unresolved cohort to the per-task gate.
            [BatchPhase.Red] = [BatchPhase.Paused, BatchPhase.Abandoned],
            [BatchPhase.Paused] = [BatchPhase.Paused, BatchPhase.Abandoned],
            [BatchPhase.Published] = [],
            [BatchPhase.Abandoned] = [],
        };

    /// <summary>The phases a batch in <paramref name="from"/> may change to; a batch without a record may only form.</summary>
    public static IReadOnlyList<BatchPhase> LegalSuccessors(BatchPhase? from)
        => from is { } phase ? Successors[phase] : [BatchPhase.Formed];

    public static bool IsLegal(BatchPhase? from, BatchPhase to) => LegalSuccessors(from).Contains(to);

    /// <summary>A terminal batch never changes phase again.</summary>
    public static bool IsTerminal(BatchPhase phase) => Successors[phase].Length == 0;

    public static BatchPhaseTransitionDecision Decide(BatchPhaseTransitionFacts facts)
    {
        if (facts.ExpectedVersion is { } expected && expected != facts.CurrentVersion)
            return Refuse(facts, StaleVersion);
        if (!IsLegal(facts.Current, facts.Requested))
            return Refuse(facts, IllegalTransition);
        // A coordinator whose lease was superseded holds a lower fence.
        if (facts.RequestedFence < facts.CurrentFence)
            return Refuse(facts, StaleFence);
        var members = facts.Current is null
            ? facts.Members.Select(member => member with { Disposition = BatchMemberDisposition.Claimed }).ToArray()
            : Dispositions(facts.Requested, facts.Members, facts.ReplayOutcomes);
        return new BatchPhaseTransitionDecision(true, null, facts.CurrentVersion + 1, members);
    }

    /// <summary>
    /// The member dispositions after the batch entered <paramref name="phase"/>.
    /// Only a terminal phase settles a claim: a published batch publishes its
    /// admitted members, an abandoned one returns them to the queue, and both
    /// leave replay ejections to the route that settles them.
    /// </summary>
    public static IReadOnlyList<BatchMemberClaim> Dispositions(BatchPhase phase,
        IReadOnlyList<BatchMemberClaim> members, IReadOnlyDictionary<string, string> replayOutcomes)
    {
        if (phase is not (BatchPhase.Published or BatchPhase.Abandoned)) return members;
        return members.Select(member =>
        {
            if (member.Disposition != BatchMemberDisposition.Claimed) return member;
            var replay = replayOutcomes.GetValueOrDefault(member.TaskKey);
            var settled = replay switch
            {
                "conflict" or "unattributable" => BatchMemberDisposition.Ejected,
                "cascade-deferred" => BatchMemberDisposition.Released,
                "admitted" when phase == BatchPhase.Published => BatchMemberDisposition.Published,
                _ when phase == BatchPhase.Abandoned => BatchMemberDisposition.Released,
                _ => BatchMemberDisposition.Claimed,
            };
            return member with { Disposition = settled };
        }).ToArray();
    }

    private static BatchPhaseTransitionDecision Refuse(BatchPhaseTransitionFacts facts, string refusal)
        => new(false, refusal, facts.CurrentVersion, facts.Members);
}
