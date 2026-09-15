using AgentStudio.Git;
using AgentStudio.Runner;

namespace AgentStudio.Tasks;

/// <summary>
/// Pure policy: which machine-readable condition a park of a given category
/// waits on. Called from the single lane-change choke point, so every park -
/// system escalation, remote review park, operator move - gets a condition
/// without touching the ~15 individual park call sites.
///
/// <para>The mapping is deliberately conservative. A category only leaves
/// <see cref="ParkedBlockerConditionKinds.Manual"/> when a probe can decide it
/// from facts the platform already owns. Claiming a checkable condition that no
/// probe can actually evaluate would produce exactly the failure this feature
/// exists to remove: a card that looks handled and is not.</para>
/// </summary>
public static class ParkedBlockerCatalog
{
    /// <summary>Blocker type for a park with no escalation category - an
    /// operator moved the card into the lane by hand.</summary>
    public const string OperatorDecision = "operator-decision";

    /// <summary>The lanes a card is parked in while it waits for a person.</summary>
    public static readonly string[] ParkedLanes = [TaskStates.HumanReview, TaskStates.Escalated];

    /// <summary>
    /// Two direct comparisons rather than a LINQ scan over
    /// <see cref="ParkedLanes"/>: this runs once per card during
    /// <c>TaskScannerService</c> hydration, which is the hot path behind
    /// <c>GET /api/tasks/grouped</c>.
    /// </summary>
    public static bool IsParkedLane(string? state)
        => string.Equals(state, TaskStates.HumanReview, StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, TaskStates.Escalated, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts the escalation category from a reason formatted by
    /// <c>HumanReviewEscalation.FormatReason</c> (<c>[category] sentence</c>).
    /// An unformatted reason is an operator park.
    /// </summary>
    public static string ReadBlockerType(string? reason)
    {
        var value = (reason ?? string.Empty).Trim();
        if (!value.StartsWith('[')) return OperatorDecision;
        var close = value.IndexOf(']');
        if (close <= 1) return OperatorDecision;
        var category = value[1..close].Trim();
        return category.Length == 0 ? OperatorDecision : category;
    }

    /// <summary>
    /// The condition a park of <paramref name="blockerType"/> waits on. Parameters
    /// are left empty on purpose: the probe resolves the repository root and
    /// branch names from the live task context, so a marker written weeks ago is
    /// evaluated against today's facts rather than against a snapshot that has
    /// since gone stale.
    /// </summary>
    public static ParkedBlockerCondition ConditionFor(string? blockerType) => blockerType switch
    {
        // AGT-2220: a baseline-comparison review cannot materialize its subject
        // while the card branch predates the integration branch. The documented
        // fix is to merge the card branch fresh onto the integration branch, so
        // the condition clears exactly when that ancestry holds.
        HumanReviewEscalationCategories.ReviewSubjectUnmaterializable => new ParkedBlockerCondition
        {
            Kind = ParkedBlockerConditionKinds.GitAncestor,
            Description = "The card branch carries the current integration branch, so a review baseline can be materialized again.",
        },

        // Everything else is a human decision, a spent budget, or an infra fault
        // with no fact the platform can re-check. Say so rather than inventing a
        // condition nothing evaluates.
        _ => new ParkedBlockerCondition
        {
            Kind = ParkedBlockerConditionKinds.Manual,
            Description = "Only a person can clear this park; no automatic precondition is recorded.",
        },
    };

    /// <summary>
    /// Builds the marker for a card that just entered <paramref name="lane"/>.
    /// Returns null when the lane is not a parked lane - the caller then clears
    /// any stale marker instead.
    /// </summary>
    public static ParkedBlockerRecord? Build(
        string? lane, string? reason, DateTime parkedAt,
        CommitWithholdingReport? withheld = null)
    {
        if (!IsParkedLane(lane)) return null;
        var text = (reason ?? string.Empty).Trim();
        var blockerType = ReadBlockerType(text);

        // A refused platform commit outranks a silent park. WEB-21 was parked
        // with an empty reason while twelve screenshots and two edited files
        // sat uncommitted, so the card could not tell anyone what was waiting.
        // Whatever else parked the card, the reason now names the gate and the
        // count, and an untyped park is typed by it.
        if (withheld is not null)
        {
            var sentence = CommitWithholdingPolicy.ParkReason(withheld);
            if (blockerType == OperatorDecision)
            {
                blockerType = HumanReviewEscalationCategories.CommitCandidatesWithheld;
                text = HumanReviewEscalation.FormatReason(blockerType, sentence);
            }
            else
            {
                text = (text + " " + sentence).Trim();
            }
        }

        return new ParkedBlockerRecord
        {
            BlockerType = blockerType,
            Condition = ConditionFor(blockerType),
            Lane = lane!,
            ParkedAt = parkedAt,
            Reason = text,
            WithheldCommitCandidates = withheld?.Withheld ?? [],
        };
    }
}
