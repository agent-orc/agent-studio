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
/// (<see cref="ParkedForSeconds"/>). AGT-2816 adds the fourth: what is the
/// question (<see cref="Decision"/>).</para>
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
public sealed record ParkedBlockerStatus(
    string BlockerType,
    string ConditionKind,
    string ConditionDescription,
    DateTime ParkedAt,
    long ParkedForSeconds,
    string Reason,
    string RecallStatus,
    DateTime? LastEvaluatedAt,
    string Detail)
{
    /// <summary>The lane the card was parked in, as recorded by the park.</summary>
    public string Lane { get; init; } = "";

    /// <summary>The question, options, and named documents of this park.</summary>
    public ParkedDecisionStatus? Decision { get; init; }

    /// <summary>Task-relative artifact holding the full agent question, when the
    /// parking run wrote one (<c>results/needs-input.md</c>).</summary>
    public string? NeedsInputFile { get; init; }

    /// <summary>How long the CURRENT verdict has held, at read time; null when
    /// no sweep has evaluated the blocker yet. An unchanged verdict is never
    /// re-persisted, so this is the verdict's own age, not a check interval.</summary>
    public long? EvaluationAgeSeconds { get; init; }

    /// <summary>
    /// True when no sweep has ever evaluated this blocker. "Nobody has checked"
    /// must read differently from "still blocked" - conflating the two is the
    /// AGT-2220 failure mode.
    /// </summary>
    public bool EvaluationStale { get; init; }

    /// <summary>True when this park is a decision for a person rather than a
    /// failure to triage, so the board can separate the two at a glance and the
    /// decision-card feature knows which parks it owns.</summary>
    public bool RequiresDecisionCard { get; init; }
}

/// <summary>Board projection of <c>ParkedDecisionRequest</c>.</summary>
/// <param name="QuestionId">The park slug, kept as an identifier only.</param>
/// <param name="Question">One sentence a person can answer; empty when the
/// parking run stated none.</param>
/// <param name="Options">The options the run had already weighed.</param>
/// <param name="Documents">Repository-relative documents the run named.</param>
/// <param name="DecisionCardKey">Linked decision card, once one exists.</param>
public sealed record ParkedDecisionStatus(
    string QuestionId,
    string Question,
    IReadOnlyList<ParkedDecisionOptionStatus> Options,
    IReadOnlyList<string> Documents,
    string? DecisionCardKey);

/// <summary>Board projection of one weighed option.</summary>
public sealed record ParkedDecisionOptionStatus(
    string Id,
    string Label,
    string? Consequences,
    bool Recommended);
