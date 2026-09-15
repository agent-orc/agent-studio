namespace AgentStudio.Shared;

/// <summary>
/// Board projection of a parked card's blocker (<see cref="TaskInfo.ParkedBlocker"/>).
/// Read-time only: it mirrors the durable <c>parked-blocker.json</c> marker in the
/// job folder and is never persisted on <c>task.json</c>.
///
/// <para>Answers the three questions a parked card could not answer before:
/// what is it waiting for (<see cref="BlockerType"/> +
/// <see cref="ConditionKind"/>), is that still true
/// (<see cref="RecallStatus"/>), and how long has it been sitting there
/// (<see cref="ParkedForSeconds"/>).</para>
/// </summary>
/// <param name="BlockerType">Escalation category that parked the card, or
/// <c>operator-decision</c> for a manual park.</param>
/// <param name="ConditionKind">Machine-readable condition kind
/// (<c>ParkedBlockerConditionKinds</c>).</param>
/// <param name="ConditionDescription">One sentence describing what must become true.</param>
/// <param name="ParkedAt">When the card entered the parked lane.</param>
/// <param name="ParkedForSeconds">Age in the parked lane at read time.</param>
/// <param name="Reason">The original freetext park reason, verbatim.</param>
/// <param name="RecallStatus">Latest sweep verdict (<c>ParkedBlockerStatuses</c>):
/// <c>blocked</c>, <c>recallable</c>, or <c>undeterminable</c>. A
/// <c>recallable</c> card is reported, never requeued automatically.</param>
/// <param name="LastEvaluatedAt">When the sweep last evaluated the condition.</param>
/// <param name="Detail">Why the sweep reached that verdict.</param>
/// <param name="WithheldCommitCandidates">Files the commit candidate gate
/// withheld from the platform commit, each with the finding code that withheld
/// it. Empty unless the park is carrying an uncommitted delivery; this is the
/// "which files and why" half that <see cref="Reason"/> can only summarize.</param>
public sealed record ParkedBlockerStatus(
    string BlockerType,
    string ConditionKind,
    string ConditionDescription,
    DateTime ParkedAt,
    long ParkedForSeconds,
    string Reason,
    string RecallStatus,
    DateTime? LastEvaluatedAt,
    string Detail,
    IReadOnlyList<AgentStudio.Git.WithheldCommitCandidate>? WithheldCommitCandidates = null);
