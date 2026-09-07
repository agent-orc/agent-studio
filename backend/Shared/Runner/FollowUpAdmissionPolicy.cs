namespace AgentStudio.Shared;

/// <summary>
/// What the admission check decided for one user follow-up (continue, steer,
/// extend, newTask) or one manual start.
/// </summary>
public enum FollowUpAdmissionAction
{
    /// <summary>The card may spawn a local CLI process right now.</summary>
    StartLocally,

    /// <summary>
    /// The card may not spawn a local process. The follow-up is persisted as a
    /// <see cref="PendingIntent"/> and the card is promoted to the top of
    /// <c>2-ready</c> so the next run - local auto-pickup or a remote claim -
    /// consumes it.
    /// </summary>
    Queue,
}

/// <summary>
/// Wire values of <see cref="ContinueJobQueuedInfo.Reason"/>. Also used as the
/// <see cref="PendingIntent.SavedReason"/> and as the qualifier of the lane
/// transition detail (<c>follow-up-queued-&lt;reason&gt;</c>), so one string
/// explains the 202 response, the saved file, and the ledger row.
/// </summary>
public static class FollowUpQueueReasons
{
    /// <summary>The project's runner slots were all taken by another task.</summary>
    public const string ProjectBusy = "project-busy";

    /// <summary>The card's lane does not admit a locally spawned follow-up run.</summary>
    public const string LaneNotRunnable = "lane-not-runnable";

    /// <summary>A finished delivery is being post-processed or judged.</summary>
    public const string DeliveryUnderReview = "delivery-under-review";

    /// <summary>The project routes execution to a remote runner.</summary>
    public const string RemoteExecution = "remote-execution";

    /// <summary>
    /// Prefix of the <see cref="PendingIntent.SavedReason"/> written when a stop
    /// path rescued an unconsumed follow-up from a run it killed. Distinguishes
    /// a rescued follow-up from one that was queued by admission, which is how
    /// the start-window check tells a lost run from a leftover queue entry.
    /// </summary>
    public const string RunStoppedPrefix = "run-stopped:";

    /// <summary>Reason string for a follow-up rescued from a stopped run.</summary>
    public static string RunStopped(string why) => RunStoppedPrefix + why;

    /// <summary>True for a <see cref="PendingIntent.SavedReason"/> written by a stop path.</summary>
    public static bool IsRunStopped(string? savedReason) =>
        savedReason?.StartsWith(RunStoppedPrefix, StringComparison.Ordinal) == true;
}

/// <summary>
/// Plain facts the admission decision needs. Read once by the caller from the
/// task record and the project's execution settings; no clock, no filesystem,
/// no process state.
/// </summary>
/// <param name="Lane">The card's current lane folder (<see cref="TaskStates"/>).</param>
/// <param name="Phase">The card's lifecycle phase (<see cref="LifecyclePhases"/>), or null.</param>
/// <param name="ExecutionLocation">
/// Resolved through <see cref="ProjectExecutionPolicy.ResolveExecutionLocation"/>:
/// <see cref="ExecutionLocations.Local"/> or the id of the assigned remote runner.
/// </param>
/// <param name="LocalRunnerId">This backend's own runner id, when known.</param>
/// <param name="LocalRunnerName">This backend's own runner name, when known.</param>
public sealed record FollowUpAdmissionFacts(
    string? Lane,
    string? Phase,
    string? ExecutionLocation,
    string? LocalRunnerId = null,
    string? LocalRunnerName = null);

/// <summary>
/// Outcome of <see cref="FollowUpAdmissionPolicy.Decide"/>.
/// <paramref name="QueueReason"/> is null exactly when
/// <paramref name="Action"/> is <see cref="FollowUpAdmissionAction.StartLocally"/>.
/// </summary>
public sealed record FollowUpAdmissionDecision(
    FollowUpAdmissionAction Action,
    string? QueueReason,
    string Detail);

/// <summary>
/// Decides whether a user follow-up may spawn a local CLI process for a card,
/// or has to be queued as a saved intent instead.
///
/// <para>
/// The rule exists because accepting a follow-up and then spawning a run the
/// lane watchdog immediately kills loses the operator's input silently: the
/// HTTP response already said <c>started</c>, the prompt only lives in the chat
/// log, and no run will ever read it (three steers lost on 2026-09-07). A card
/// outside <c>3-progress</c>/<c>2-ready</c>, a card whose delivery is being
/// reviewed, and a card routed to a remote runner all produce a run that either
/// gets killed by lane reconciliation or collides with the owning runner. Those
/// cases are queued, never started here.
/// </para>
///
/// <para>
/// Queueing is not a rejection: the caller persists the prompt as
/// <c>pending-intent.json</c> and promotes the card to the top of
/// <c>2-ready</c>, where the next local pickup or remote claim consumes it.
/// </para>
/// </summary>
public static class FollowUpAdmissionPolicy
{
    /// <summary>
    /// The lanes a local follow-up run may be spawned from. <c>3-progress</c> is
    /// where a run belongs; <c>2-ready</c> is admitted because the runner's own
    /// pickup moves it to <c>3-progress</c> before spawning.
    ///
    /// <para>
    /// Everything else is queued - including the two park lanes
    /// (<c>3a-failed-pickup</c>, <c>3b-code-not-complete</c>). They look
    /// runnable but are not: <c>RunPlanner</c> does not move them to
    /// <c>3-progress</c>, so a run started there is killed by the same lane
    /// reconciliation that started this ticket.
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> RunnableLanes =
        new HashSet<string>(StringComparer.Ordinal) { TaskStates.Ready, TaskStates.Progress };

    /// <summary>
    /// Phases that mean a delivery has been handed on and is being
    /// post-processed or judged. A follow-up sent while one of these is active
    /// races the auto-review worker: the worker moves the card out of
    /// <c>3-progress</c> and the fresh process dies with
    /// "active job moved out of 3-progress".
    /// </summary>
    public static readonly IReadOnlySet<string> DeliveryReviewPhases =
        new HashSet<string>(StringComparer.Ordinal)
        {
            LifecyclePhases.PostProcessingRunning,
            LifecyclePhases.PostProcessingBlocked,
            LifecyclePhases.AwaitingReview,
            LifecyclePhases.Integrating,
        };

    public static FollowUpAdmissionDecision Decide(FollowUpAdmissionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var lane = facts.Lane ?? string.Empty;
        if (!RunnableLanes.Contains(lane))
        {
            return new FollowUpAdmissionDecision(
                FollowUpAdmissionAction.Queue,
                FollowUpQueueReasons.LaneNotRunnable,
                $"lane '{(string.IsNullOrEmpty(lane) ? "<unknown>" : lane)}' does not admit a locally started follow-up");
        }

        var phase = facts.Phase;
        if (!string.IsNullOrWhiteSpace(phase) && DeliveryReviewPhases.Contains(phase))
        {
            return new FollowUpAdmissionDecision(
                FollowUpAdmissionAction.Queue,
                FollowUpQueueReasons.DeliveryUnderReview,
                $"a delivery is under review (phase '{phase}')");
        }

        if (ResolveRemoteRunner(facts) is { } remoteRunner)
        {
            return new FollowUpAdmissionDecision(
                FollowUpAdmissionAction.Queue,
                FollowUpQueueReasons.RemoteExecution,
                $"execution is routed to remote runner '{remoteRunner}'");
        }

        return new FollowUpAdmissionDecision(
            FollowUpAdmissionAction.StartLocally,
            null,
            $"lane '{lane}' admits a locally started follow-up");
    }

    /// <summary>
    /// True when an intent observed on a freshly started card proves the run was
    /// stopped instead of started, so the endpoint must answer <c>409</c> rather
    /// than <c>started</c>.
    ///
    /// <para>
    /// Two guards keep this from crying wolf. Only a stop path writes a
    /// <see cref="FollowUpQueueReasons.RunStoppedPrefix"/> reason, so an intent
    /// queued by admission is ignored; and an intent the card already carried
    /// before the start (an operator continuing a card that still holds an
    /// unconsumed follow-up) is ignored because it is not evidence about
    /// <em>this</em> run.
    /// </para>
    /// </summary>
    public static bool IsStartWindowLoss(PendingIntent? before, PendingIntent? observed)
    {
        if (observed is null) return false;
        if (!FollowUpQueueReasons.IsRunStopped(observed.SavedReason)) return false;
        return before is null || before.SavedAt != observed.SavedAt;
    }

    /// <summary>
    /// The remote runner that owns execution for this card, or null when the
    /// local backend may run it. A configured location that names this backend's
    /// own identity is local, not remote.
    /// </summary>
    private static string? ResolveRemoteRunner(FollowUpAdmissionFacts facts)
    {
        var location = ExecutionLocations.Normalize(facts.ExecutionLocation);
        if (string.Equals(location, ExecutionLocations.Local, StringComparison.Ordinal)) return null;
        if (string.Equals(location, facts.LocalRunnerId, StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(location, facts.LocalRunnerName, StringComparison.OrdinalIgnoreCase)) return null;
        return location;
    }
}
