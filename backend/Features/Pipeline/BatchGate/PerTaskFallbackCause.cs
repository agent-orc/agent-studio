namespace AgentStudio.Pipeline;

/// <summary>
/// The rules that send a review generation to a per-task fallback gate instead
/// of a batch (AGT-3466). The batch gate state names the rule per member.
/// </summary>
public static class PerTaskFallbackRules
{
    /// <summary>The planner chose a per-task gate for the card.</summary>
    public const string PlannerPerTask = "planner-per-task";

    /// <summary>A code delivery under a documentation-only batch scope.</summary>
    public const string DocumentationScope = "documentation-scope";

    /// <summary>The batch gate is off or the project integrates post-merge.</summary>
    public const string BatchDisabled = "batch-disabled";

    /// <summary>The member's batch paused for a reason other than a red integration branch.</summary>
    public const string BatchPaused = "batch-paused";

    /// <summary>The member's batch ejected it, for example after red isolation.</summary>
    public const string BatchEjected = "batch-ejected";

    /// <summary>The batch replay could not attribute the member's commits.</summary>
    public const string UnattributableReplay = "unattributable-replay";

    /// <summary>An operator ejected the card from the batch queue.</summary>
    public const string OperatorEject = "operator-eject";

    /// <summary>The integration branch already contains the result; no gate runs.</summary>
    public const string AlreadyIntegrated = "already-integrated";

    /// <summary>Batch admission failed when the review settled.</summary>
    public const string EmergencyAdmission = "emergency-admission";

    /// <summary>
    /// A durable per-task gate marker had no gate in this process, after a
    /// restart or crash. Recorded only when no earlier cause is known.
    /// </summary>
    public const string InterruptedGate = "interrupted-gate";

    /// <summary>
    /// AGT-3740: the passed review ran its own build and tests instead of
    /// deferring them, so batch admission refuses it. The event names the
    /// card's route: per-task integration or a re-planned review.
    /// </summary>
    public const string ReviewRanBuildTests = "review-ran-build-tests";
}

/// <param name="Rule">One of <see cref="PerTaskFallbackRules"/>.</param>
/// <param name="Event">What triggered the rule, in operator words.</param>
/// <param name="BatchId">The batch the member left, if any.</param>
public sealed record PerTaskFallbackCause(string Rule, string Event, string? BatchId = null);
