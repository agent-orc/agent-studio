namespace AgentStudio.Runner;

/// <summary>
/// What a user follow-up (continue / steer / extend / newTask) may do right
/// now on its card.
/// </summary>
public enum FollowUpAdmission
{
    /// <summary>The card may spawn a local CLI process for this follow-up.</summary>
    StartLocally,
    /// <summary>
    /// The follow-up must be persisted as a pending intent and the card
    /// promoted to the top of <c>2-ready</c>; the next run (local auto-pickup
    /// or a remote claim) consumes it.
    /// </summary>
    Queue,
}

/// <summary>
/// Closed vocabulary for <c>ContinueJobQueuedInfo.Reason</c>. Written to
/// <c>pending-intent.json</c> as <c>savedReason</c> and echoed on the wire, so
/// the operator sees the same word in the API response, on the card, and in the
/// timeline.
/// </summary>
public static class FollowUpQueueReasons
{
    /// <summary>The project was already executing another job (pre-existing reason).</summary>
    public const string ProjectBusy = "project-busy";
    /// <summary>The card's lane does not start follow-up runs at all.</summary>
    public const string LaneNotRunnable = "lane-not-runnable";
    /// <summary>The card has a finished delivery waiting for a review verdict.</summary>
    public const string DeliveryUnderReview = "delivery-under-review";
    /// <summary>The project routes execution to a remote runner, not this backend.</summary>
    public const string RemoteExecution = "remote-execution";

    /// <summary>
    /// True when a saved intent came from follow-up admission rather than from
    /// another producer (steer-timeout auto-answer, integration recovery).
    ///
    /// <para>
    /// Load-bearing distinction: admission appends the operator's text to
    /// <c>prompt.md</c> before it queues, so any later run on that card already
    /// carries the words. Other producers do not - <c>SteerTimeoutMonitor</c>
    /// stores its auto-answer only in <c>pending-intent.json</c> - so their
    /// intents must survive until a run genuinely consumes them. Treating the
    /// two alike would re-create the very loss this policy exists to prevent.
    /// </para>
    /// </summary>
    public static bool IsAdmissionQueued(string? savedReason) =>
        savedReason is ProjectBusy or LaneNotRunnable or DeliveryUnderReview or RemoteExecution;
}

/// <summary>
/// One admission verdict. <see cref="QueueReason"/> is null exactly when
/// <see cref="Outcome"/> is <see cref="FollowUpAdmission.StartLocally"/>;
/// <see cref="Explanation"/> is the operator-facing sentence that travels into
/// the chat line, the orchestrator feed, and the supervisor advisory.
/// </summary>
public sealed record FollowUpAdmissionDecision(
    FollowUpAdmission Outcome,
    string? QueueReason,
    string Explanation)
{
    public bool Queues => Outcome == FollowUpAdmission.Queue;
}

/// <summary>
/// Lane-aware admission for a user follow-up, evaluated <b>before</b> anything
/// is spawned (AGT-2747).
///
/// <para>
/// The bug this policy exists to prevent: <c>POST /api/tasks/{id}/continue</c>
/// used to accept a follow-up for a card in any lane, answer
/// <c>200 {"status":"started"}</c>, and hand it to a local CLI run. When the
/// card was not physically in <c>3-progress</c> the lane watchdog
/// (<c>ProjectRunner.ReconcileActiveJobAgainstDisk</c>) killed the fresh
/// process within a second with "active job moved out of 3-progress", and
/// nothing had persisted the prompt - three operator steers were lost that way
/// on 2026-09-07. Only a card that is already in a lane a local run may own can
/// start a local process; every other case is queued with a durable intent.
/// </para>
///
/// <para>
/// Deliberately pure: lane, phase, and the project's configured execution
/// location in, a verdict out. No filesystem, no clock, no runner state - so
/// the matrix is testable directly (see the .NET style guide's "pure policy
/// first" rule and <c>FollowUpAdmissionPolicyTests</c>).
/// </para>
/// </summary>
public static class FollowUpAdmissionPolicy
{
    /// <summary>
    /// The only two lanes a local follow-up run may start from. <c>3-progress</c>
    /// is where a run already lives; <c>2-ready</c> is the lane
    /// <c>RunCliAsync</c> promotes to <c>3-progress</c> before it spawns, so a
    /// run admitted here is always physically in <c>3-progress</c> by the time
    /// the lane watchdog can look at it.
    /// </summary>
    public static readonly IReadOnlyList<string> RunnableLanes =
        [TaskStates.Ready, TaskStates.Progress];

    /// <summary>True when a local follow-up run may start from <paramref name="lane"/>.</summary>
    public static bool IsRunnableLane(string? lane) =>
        !string.IsNullOrWhiteSpace(lane)
        && RunnableLanes.Contains(lane, StringComparer.Ordinal);

    /// <summary>
    /// True when the card carries a finished delivery that is waiting for a
    /// verdict. Both phases mean "the agent is done and something else owns the
    /// card now", so re-entering the CLI session would race the reviewer.
    ///
    /// <para>
    /// <c>steer-pending</c> is deliberately NOT in this set: the UI-iteration
    /// and concept sight-review gates are designed to receive the operator's
    /// verdict through the ordinary continue path
    /// (<see cref="UiIterationGate.IsFeedbackContinuation"/>), and queueing
    /// those would break the human gate they implement.
    /// </para>
    /// </summary>
    public static bool IsDeliveryUnderReview(string? phase) =>
        string.Equals(phase, LifecyclePhases.AwaitingReview, StringComparison.Ordinal)
        || string.Equals(phase, LifecyclePhases.Integrating, StringComparison.Ordinal);

    /// <summary>
    /// Decides whether a follow-up may spawn locally. Rule order mirrors how an
    /// operator would reason about the card: which lane is it in, is a delivery
    /// already under review, and does this backend own execution at all.
    /// </summary>
    /// <param name="lane">The card's current <see cref="TaskStates"/> value.</param>
    /// <param name="phase">The card's <see cref="LifecyclePhases"/> substate, or null.</param>
    /// <param name="isLocalExecution">
    /// <c>ProjectExecutionPolicy.IsLocalExecution</c> for the owning project.
    /// </param>
    /// <param name="configuredRunnerId">
    /// The project's configured execution location, used only to name the remote
    /// runner in the explanation.
    /// </param>
    public static FollowUpAdmissionDecision Decide(
        string? lane,
        string? phase,
        bool isLocalExecution,
        string? configuredRunnerId = null)
    {
        if (!IsRunnableLane(lane))
        {
            return new FollowUpAdmissionDecision(
                FollowUpAdmission.Queue,
                FollowUpQueueReasons.LaneNotRunnable,
                $"Lane '{lane ?? "<unknown>"}' does not run follow-ups; only " +
                $"{TaskStates.Ready} and {TaskStates.Progress} may start one.");
        }

        if (IsDeliveryUnderReview(phase))
        {
            return new FollowUpAdmissionDecision(
                FollowUpAdmission.Queue,
                FollowUpQueueReasons.DeliveryUnderReview,
                $"A finished delivery is waiting for a review verdict (phase '{phase}').");
        }

        if (!isLocalExecution)
        {
            var runner = string.IsNullOrWhiteSpace(configuredRunnerId)
                ? "a remote runner"
                : $"runner '{configuredRunnerId}'";
            return new FollowUpAdmissionDecision(
                FollowUpAdmission.Queue,
                FollowUpQueueReasons.RemoteExecution,
                $"Execution for this project is routed to {runner}, not this backend.");
        }

        return new FollowUpAdmissionDecision(
            FollowUpAdmission.StartLocally,
            null,
            $"Lane '{lane}' owns local execution for this project.");
    }
}
