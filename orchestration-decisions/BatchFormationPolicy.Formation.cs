using AgentStudio.TaskServer.Contracts.BatchGate;

namespace AgentStudio.OrchestrationDecisions;

/// <summary>The route a plan decision takes the scope: close a batch, per-task gates, or wait.</summary>
public enum BatchFormationRoute
{
    Wait,
    Close,
    PerTask,
}

/// <summary>
/// The members a close takes, in proposal order and at most the maximum
/// size. <paramref name="BelowMinimum"/> is true when fewer than the minimum
/// size remain; no batch closes then.
/// </summary>
public sealed record BatchCloseSelection(IReadOnlyList<string> SelectedKeys, bool BelowMinimum);

/// <summary>The plan decision and its reason the threshold planner records for one scope.</summary>
public sealed record BatchThresholdPlan(string Decision, string Reason);

/// <summary>Why formation admission keeps a member out, or <see cref="Admit"/>.</summary>
public enum BatchAdmissionVerdict
{
    Admit,
    SupersededGeneration,
    PolicyExcluded,
    OperatorEject,
}

/// <summary>What a member's replay onto the proposed tip means for admission (AGT-3104).</summary>
public enum BatchAdmissionReplay
{
    Admit,
    Conflict,
    EjectUnattributable,
    Pause,
}

/// <summary>The result of one formation admission pass.</summary>
public enum BatchAdmissionOutcome
{
    /// <summary>Every proposed member was admitted; the formation stays as proposed.</summary>
    Unchanged,

    /// <summary>Some members were rejected; the formation closes over the admitted ones.</summary>
    Trimmed,

    /// <summary>The integration base moved; the next tick plans on the fetched tip.</summary>
    BaseMoved,

    /// <summary>A replacement lost a member; the formed batches it would retire keep waiting.</summary>
    ReplacementChanged,

    /// <summary>Fewer members than the minimum size were admitted; nothing closes.</summary>
    BelowMinimum,
}

/// <summary>
/// AGT-3568: snapshot, admission and close decisions of the formation
/// planner, next to repack and the intent commit check. The pilot collects
/// the facts and executes the result.
/// </summary>
public static partial class BatchFormationPolicy
{
    /// <summary>
    /// Close rule: the proposed keys that are eligible, in proposal order,
    /// without duplicates and at most <paramref name="maximumSize"/>.
    /// </summary>
    public static BatchCloseSelection CloseSelection(IReadOnlyCollection<string> eligibleKeys,
        IReadOnlyList<string> proposedKeys, int minimumSize, int maximumSize)
    {
        var eligible = eligibleKeys.ToHashSet(StringComparer.Ordinal);
        var selected = proposedKeys.Distinct(StringComparer.Ordinal)
            .Where(eligible.Contains).Take(maximumSize).ToArray();
        return new(selected, selected.Length < minimumSize);
    }

    /// <summary>The route of a look-ahead decision (<c>close</c>, <c>per-task</c>, otherwise wait).</summary>
    public static BatchFormationRoute LookAheadRoute(string decision)
        => decision switch
        {
            "close" => BatchFormationRoute.Close,
            "per-task" => BatchFormationRoute.PerTask,
            _ => BatchFormationRoute.Wait,
        };

    /// <summary>A close or per-task decision consumes an operator's close-now request.</summary>
    public static bool ConsumesCloseNow(BatchFormationRoute route) => route != BatchFormationRoute.Wait;

    /// <summary>
    /// A look-ahead close proposes the members already proven green on the
    /// base first, then the decision's proposed members.
    /// </summary>
    public static IReadOnlyList<string> LookAheadCloseKeys(IEnumerable<string> provenGreenKeys,
        IEnumerable<string> proposedMembers)
        => provenGreenKeys.Concat(proposedMembers).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Snapshot rule: the members the planning snapshot (look-ahead) or an
    /// operator's close-now (threshold) considers are those no scope rule excludes.
    /// </summary>
    public static IReadOnlyList<string> SnapshotEligible(IEnumerable<(string TaskKey, bool Excluded)> members)
        => members.Where(member => !member.Excluded).Select(member => member.TaskKey).ToArray();

    /// <summary>
    /// A recorded plan names every excluded pending member of the project and
    /// never proposes one of them.
    /// </summary>
    public static BatchPlanDecision WithExcluded(BatchPlanDecision plan,
        IReadOnlyList<BatchPlanExcludedMember> excluded)
        => plan with
        {
            ExcludedMembers = excluded,
            ProposedMembers = plan.ProposedMembers.Where(key =>
                !excluded.Any(item => item.TaskKey == key)).ToArray(),
        };

    /// <summary>The plan decision the threshold planner records for its formation result.</summary>
    public static BatchThresholdPlan ThresholdPlan(bool closed, bool perTask, string? formationReason,
        int queuedPartners, int eligibleCount, int currentCount)
    {
        var reason = closed ? "threshold reached"
            : perTask ? formationReason ?? "per-task gate selected"
            : queuedPartners > 0 ? BatchGateLookAheadPlanner.QueuedPartnersReason
            : "waiting for threshold or deadline";
        var decision = closed ? "close"
            : perTask ? "per-task"
            : eligibleCount == 0 && currentCount == 0 && queuedPartners == 0 ? "idle" : "wait";
        return new(decision, reason);
    }

    /// <summary>
    /// Admission checks of a still pending member before any Git probe, in
    /// order: its generation current, the subject eligible for the scope and
    /// the member not ejected by an operator.
    /// </summary>
    public static BatchAdmissionVerdict AdmitMember(bool generationSuperseded, bool policyExcluded,
        bool operatorEject)
        => generationSuperseded ? BatchAdmissionVerdict.SupersededGeneration
            : policyExcluded ? BatchAdmissionVerdict.PolicyExcluded
            : operatorEject ? BatchAdmissionVerdict.OperatorEject
            : BatchAdmissionVerdict.Admit;

    /// <summary>
    /// A conflicted replay leaves as a conflict round. A success is admitted
    /// only with a tip and a complete commit mapping; an unattributable
    /// member is ejected alone; any other failure pauses formation.
    /// </summary>
    public static BatchAdmissionReplay AdmitReplay(int conflictedFiles, bool success, bool hasTip,
        bool mappingComplete, bool unattributableFailure)
        => conflictedFiles > 0 ? BatchAdmissionReplay.Conflict
            : success ? hasTip && mappingComplete ? BatchAdmissionReplay.Admit : BatchAdmissionReplay.EjectUnattributable
            : unattributableFailure ? BatchAdmissionReplay.EjectUnattributable
            : BatchAdmissionReplay.Pause;

    /// <summary>
    /// The result of an admission pass. A moved base wins, then a replacement
    /// that lost any member, then the minimum size.
    /// </summary>
    public static BatchAdmissionOutcome AdmissionOutcome(bool baseMoved, bool replacement, int rejected,
        int admitted, int minimumSize)
        => baseMoved ? BatchAdmissionOutcome.BaseMoved
            : replacement && rejected > 0 ? BatchAdmissionOutcome.ReplacementChanged
            : admitted < minimumSize ? BatchAdmissionOutcome.BelowMinimum
            : rejected == 0 ? BatchAdmissionOutcome.Unchanged
            : BatchAdmissionOutcome.Trimmed;
}
