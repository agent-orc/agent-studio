namespace AgentStudio.Shared;

/// <summary>
/// AGT-2818 - the mechanism that holds a card in a pickup lane. A card in
/// <c>2-ready</c> that the pickup gate skips is not "queued", it is held, and
/// the board has to be able to say which of these is doing the holding.
/// </summary>
public static class PickupHoldMechanisms
{
    /// <summary>A <c>dependsOn</c> edge is unfulfilled, unsatisfiable, or cyclic.</summary>
    public const string DependencyGate = "dependency-gate";

    /// <summary>A remote runner refused the offered card and recorded why.</summary>
    public const string DispatchRejection = "dispatch-rejection";

    /// <summary>An epic container came to rest in a pickup lane. Epics never execute.</summary>
    public const string EpicContainer = "epic-container";

    /// <summary>The card crashed fast and is serving its exponential cooldown.</summary>
    public const string CrashBackoff = "crash-backoff";

    /// <summary>
    /// A pickup-policy rule refuses the card: it is assigned to a human, it is
    /// a fixture, it is a human-decision marker, or intake has not passed it.
    /// </summary>
    public const string PickupPolicy = "pickup-policy";
}

/// <summary>
/// Machine-readable kinds for the ways out of a hold. The UI keys its
/// affordances off these rather than off prose, so a wording change never
/// silently disarms a button.
/// </summary>
public static class PickupHoldResolutionKinds
{
    public const string DropDependency = "drop-dependency";
    public const string RepointDependency = "repoint-dependency";
    public const string ArchiveWaitingCard = "archive-waiting-card";
    /// <summary>Grant the target's explicit release flag (<c>PUT /api/tasks/{id}/release</c>).</summary>
    public const string ReleaseTarget = "release-target";

    /// <summary>Drop the <c>releaseGate</c> edge and re-plan the dependent.</summary>
    public const string DropReleaseGate = "drop-release-gate";

    /// <summary>Finish, or drop the edge to, a target that has not reached a terminal lane.</summary>
    public const string AwaitTarget = "await-target";

    /// <summary>Create the referenced key, or remove the edge that names it.</summary>
    public const string CreateOrDropTarget = "create-or-drop-target";

    /// <summary>Break the dependsOn cycle through the task's references.</summary>
    public const string BreakCycle = "break-cycle";

    /// <summary>Give the runner the missing capability, or route the card elsewhere.</summary>
    public const string RestoreRunnerCapability = "restore-runner-capability";

    /// <summary>Re-offer the card so a healthy runner can claim it.</summary>
    public const string RetryDispatch = "retry-dispatch";

    /// <summary>Decompose the epic; its sub-tasks are what execute.</summary>
    public const string DecomposeEpic = "decompose-epic";

    /// <summary>Wait out the cooldown, or start the run by hand.</summary>
    public const string WaitOutBackoff = "wait-out-backoff";

    /// <summary>Assign the card to a CLI agent instead of a human.</summary>
    public const string AssignAgent = "assign-agent";

    /// <summary>Let intake pass the card, or disable intake for the project.</summary>
    public const string PassIntake = "pass-intake";

    /// <summary>Route the human decision through the escalated lane.</summary>
    public const string DecideEscalation = "decide-escalation";
}

/// <summary>
/// One way an operator can clear a hold. Offered, never taken: releasing a
/// validation gate is a decision about whether the validation still has to
/// happen, and nothing in this projection is allowed to make it.
/// </summary>
/// <param name="Kind">One of <see cref="PickupHoldResolutionKinds"/>.</param>
/// <param name="Label">Short imperative label for a button or list row.</param>
/// <param name="Detail">One sentence naming what the operator is deciding.</param>
/// <param name="TargetKey">The stable key the resolution acts on, when it has one.</param>
public sealed record PickupHoldResolution(
    string Kind,
    string Label,
    string Detail,
    string? TargetKey = null);

/// <summary>
/// AGT-2818 - read-time projection of the queued-but-unpickable state of a card
/// sitting in a pickup lane. Never persisted to <c>task.json</c>.
///
/// <para>The reported incident: two cards sat in <c>2-ready</c> for weeks while
/// the pickup gate skipped them on every tick, and in both cases the reason was
/// already recorded in the product and simply not shown. The board read the lane
/// as "the system is standing". This record is the sentence the card was
/// missing: the mechanism, the specific reason, when it started, how long it has
/// been true, and what would clear it.</para>
///
/// <para>Derived by <see cref="PickupHoldPolicy"/> from the same facts the
/// runner admission gate consults, in the runner's own check order, so the board
/// and the admission decision can never disagree.</para>
/// </summary>
public sealed record PickupHoldStatus
{
    /// <summary>Dependency liveness class: satisfiable-soon, stalled, or unsatisfiable.</summary>
    public string Classification { get; init; } = PickupHoldClassifications.SatisfiableSoon;
    /// <summary>One of <see cref="PickupHoldMechanisms"/>.</summary>
    public string Mechanism { get; init; } = "";

    /// <summary>The specific reason, in one operator-facing sentence.</summary>
    public string Reason { get; init; } = "";

    /// <summary>
    /// When this hold started: the rejection instant for a refused dispatch, the
    /// cooldown start for a crash backoff, otherwise the card's lane entry.
    /// </summary>
    public DateTime SinceUtc { get; init; }

    /// <summary>Age of the hold at read time, so the card can say "held for 34 days".</summary>
    public long HeldForSeconds { get; init; }

    /// <summary>
    /// True when nothing the system does on its own will ever clear this hold -
    /// an archived release gate or a dependsOn cycle. A configuration error, not
    /// a wait.
    /// </summary>
    public bool Unsatisfiable { get; init; }

    /// <summary>The ways out, in the order they should be offered. Never applied automatically.</summary>
    public List<PickupHoldResolution> Resolutions { get; init; } = [];

    /// <summary>The prerequisite key represented by one deduplicated attention item.</summary>
    public string? AttentionTargetKey { get; init; }

    /// <summary>The prerequisite's own blocker summary, rather than a waiting card's copy.</summary>
    public string? AttentionReason { get; init; }

    /// <summary>When the prerequisite itself entered the stalled condition.</summary>
    public DateTime? AttentionSinceUtc { get; init; }
}

public static class PickupHoldClassifications
{
    public const string SatisfiableSoon = "satisfiable-soon";
    public const string Stalled = "stalled";
    public const string Unsatisfiable = "unsatisfiable";
}

/// <summary>
/// The facts <see cref="PickupHoldPolicy"/> needs, gathered by the caller. The
/// policy itself performs no lookups so it stays a pure decision matrix that is
/// unit-testable without a runner, a scanner, or a web host.
/// </summary>
/// <param name="Task">The card sitting in a pickup lane.</param>
/// <param name="WaitsOn">Its waits-on status, or null when it has no dependsOn edges.</param>
/// <param name="IntakeEnabled">Whether the project runs the orchestrator-intake gate.</param>
/// <param name="CrashBackoffUntilUtc">Armed rapid-crash cooldown deadline, when any.</param>
/// <param name="Rejection">Latest runner refusal for the current lane stay, when any.</param>
/// <param name="NowUtc">Read-time clock, for the age.</param>
public readonly record struct PickupHoldFacts(
    TaskInfo Task,
    WaitsOnStatus? WaitsOn,
    bool IntakeEnabled,
    DateTime? CrashBackoffUntilUtc,
    RemoteDispatchRejection? Rejection,
    DateTime NowUtc);

/// <summary>
/// Pure policy: is this card held in its pickup lane, and by what.
///
/// <para>The check order mirrors <c>ProjectRunner.IsReadyPickupCandidate</c>
/// exactly, so the mechanism the card names is the one that actually decided the
/// skip. The dispatch rejection is checked last on purpose: a card the local gate
/// already refuses was never offered to a runner, so a stale refusal from an
/// earlier tick must not outrank the gate that is doing the holding now.</para>
/// </summary>
public static class PickupHoldPolicy
{
    public static readonly TimeSpan DefaultStalledThreshold = TimeSpan.FromMinutes(30);
    /// <summary>
    /// The lanes a card can claim to be queued in. <c>3-progress</c> is
    /// deliberately absent: a card there has been picked up, so "queued but
    /// unpickable" is not a state it can be in.
    /// </summary>
    public static bool IsPickupLane(string? state) =>
        string.Equals(state, TaskStates.Ready, StringComparison.Ordinal);

    /// <summary>
    /// Returns the hold on <paramref name="facts"/>, or null when the card is
    /// genuinely pickup-eligible and its lane position is honest.
    /// </summary>
    public static PickupHoldStatus? Evaluate(PickupHoldFacts facts)
    {
        var task = facts.Task;
        if (task is null || !IsPickupLane(task.State)) return null;

        var laneEntry = task.EnteredLaneAt == default ? task.CreatedAt : task.EnteredLaneAt;

        if (!AgentTypes.IsAutoPickupEligible(task.Agent))
            return Hold(
                PickupHoldMechanisms.PickupPolicy,
                "This card is assigned to a human agent, so the runner never auto-picks it.",
                laneEntry, facts.NowUtc, unsatisfiable: false,
                new PickupHoldResolution(
                    PickupHoldResolutionKinds.AssignAgent,
                    "Assign a CLI agent",
                    "Change the card's agent from human to a CLI agent, or start the run by hand."));

        if (task.Fixture)
            return Hold(
                PickupHoldMechanisms.PickupPolicy,
                "This card is a fixture, which exists for tests and is never executed.",
                laneEntry, facts.NowUtc, unsatisfiable: false);

        if (TaskKinds.IsEpic(task.Kind))
            return Hold(
                PickupHoldMechanisms.EpicContainer,
                "This is an epic container. Epics hold sub-tasks and never execute themselves.",
                laneEntry, facts.NowUtc, unsatisfiable: false,
                new PickupHoldResolution(
                    PickupHoldResolutionKinds.DecomposeEpic,
                    "Decompose the epic",
                    "Plan the epic so its sub-tasks carry the work, then move the container out of the pickup lane."));

        if (TaskSlugs.IsHumanDecisionNeeded(task.Id))
            return Hold(
                PickupHoldMechanisms.PickupPolicy,
                "This card is a human-decision marker, so the runner refuses to spawn a run for it.",
                laneEntry, facts.NowUtc, unsatisfiable: false,
                new PickupHoldResolution(
                    PickupHoldResolutionKinds.DecideEscalation,
                    "Decide it in the escalated lane",
                    "Move the card to 5e-escalated and record the decision there."));

        if (facts.CrashBackoffUntilUtc is { } until && until > facts.NowUtc)
            return Hold(
                PickupHoldMechanisms.CrashBackoff,
                $"The last run crashed immediately, so pickup is on cooldown until {until:u}.",
                laneEntry, facts.NowUtc, unsatisfiable: false,
                new PickupHoldResolution(
                    PickupHoldResolutionKinds.WaitOutBackoff,
                    "Wait out the cooldown",
                    "The cooldown expires on its own; start the run by hand if the crash cause is already fixed."));

        if (facts.IntakeEnabled && task.Phase != LifecyclePhases.IntakePassed)
            return Hold(
                PickupHoldMechanisms.PickupPolicy,
                $"Project intake is enabled and this card has not passed it (phase: {Describe(task.Phase)}).",
                laneEntry, facts.NowUtc, unsatisfiable: false,
                new PickupHoldResolution(
                    PickupHoldResolutionKinds.PassIntake,
                    "Let intake pass the card",
                    "Resolve what intake is blocking on, or disable the intake gate for this project."));

        var dependency = DependencyHold(facts, laneEntry);
        if (dependency != null) return dependency;

        // Dated from the refusal, not from the lane entry: that is the moment the
        // card stopped being pickable, and it is the number the operator needs.
        if (facts.Rejection is { } rejection)
            return Hold(
                PickupHoldMechanisms.DispatchRejection,
                $"Runner {Describe(rejection.RunnerName, rejection.RunnerId)} refused this card ({rejection.Code}): {rejection.Reason}",
                rejection.RejectedAtUtc, facts.NowUtc, unsatisfiable: false,
                new PickupHoldResolution(
                    PickupHoldResolutionKinds.RestoreRunnerCapability,
                    "Restore the runner capability",
                    $"Give {Describe(rejection.RunnerName, rejection.RunnerId)} what it reported missing, or route this project at a runner that has it."),
                new PickupHoldResolution(
                    PickupHoldResolutionKinds.RetryDispatch,
                    "Re-offer the card",
                    "The refusal stays visible until a later dispatch succeeds or an operator clears it."));

        return null;
    }

    /// <summary>
    /// The dependency-gate branch. A cycle and an archived release gate are both
    /// configuration errors and are reported as unsatisfiable; everything else is
    /// an honest wait.
    /// </summary>
    private static PickupHoldStatus? DependencyHold(PickupHoldFacts facts, DateTime laneEntry)
    {
        var waitsOn = facts.WaitsOn;
        if (waitsOn == null || !waitsOn.Blocked) return null;

        if (waitsOn.CycleDetected)
            return Hold(
                PickupHoldMechanisms.DependencyGate,
                "This card's dependsOn chain forms a cycle, so no order of completions can ever fulfil it.",
                laneEntry, facts.NowUtc, unsatisfiable: true,
                DependencyDecisions(waitsOn.Items.FirstOrDefault()?.Key).ToArray());

        var unsatisfiable = waitsOn.Items.FirstOrDefault(item => item.Unsatisfiable);
        if (unsatisfiable != null)
            return Hold(
                PickupHoldMechanisms.DependencyGate,
                unsatisfiable.UnsatisfiableReason.Length > 0
                    ? unsatisfiable.UnsatisfiableReason
                    : WaitsOnEvaluator.ArchivedGateReason(unsatisfiable.Key),
                laneEntry, facts.NowUtc, unsatisfiable: true,
                DependencyDecisions(unsatisfiable.Key).ToArray());

        var open = waitsOn.Items.FirstOrDefault(item => !item.Fulfilled);
        if (open == null) return null;

        if (open.WaitingForRelease)
            return Hold(
                PickupHoldMechanisms.DependencyGate,
                $"{open.Key} is terminal but carries no explicit release, and a release gate needs one.",
                laneEntry, facts.NowUtc, unsatisfiable: false,
                new PickupHoldResolution(
                    PickupHoldResolutionKinds.ReleaseTarget,
                    $"Release {open.Key}",
                    "Releasing states that the validation this gate stands for no longer has to happen. Only an operator may decide that.",
                    open.Key),
                new PickupHoldResolution(
                    PickupHoldResolutionKinds.DropReleaseGate,
                    $"Drop the release gate on {open.Key}",
                    "Remove the releaseGate edge through this card's references and re-plan the card.",
                    open.Key));

        if (!open.Resolved)
            return Hold(
                PickupHoldMechanisms.DependencyGate,
                $"{open.Key} does not exist in the workspace, so nothing can fulfil this edge.",
                laneEntry, facts.NowUtc, unsatisfiable: true,
                DependencyDecisions(open.Key).ToArray());

        var stalled = IsStalled(open, facts.NowUtc);
        var hold = HoldClassified(
                PickupHoldMechanisms.DependencyGate,
                $"{open.Key} has not reached a terminal lane yet (currently {Describe(open.TargetState)}).",
                laneEntry, facts.NowUtc, unsatisfiable: false,
                stalled ? PickupHoldClassifications.Stalled : PickupHoldClassifications.SatisfiableSoon,
                new PickupHoldResolution(
                    PickupHoldResolutionKinds.AwaitTarget,
                    stalled ? $"Resolve {open.Key}'s blocker" : $"Finish {open.Key}",
                    stalled
                        ? $"Open {open.Key} and resolve its parked or timed-out prerequisite state."
                        : "The gate opens on its own once the target reaches 6-completed or 7-archive.",
                    open.Key));
        return stalled
            ? hold with
            {
                AttentionTargetKey = open.Key,
                AttentionReason = StalledAttentionReason(open),
                AttentionSinceUtc = open.TargetBlockerSinceUtc ?? open.TargetEnteredLaneAt,
            }
            : hold;
    }

    private static bool IsStalled(WaitsOnItem target, DateTime now)
    {
        if (target.TargetParked || string.Equals(target.TargetState, TaskStates.Escalated, StringComparison.Ordinal))
            return true;
        if (!string.Equals(target.TargetState, TaskStates.AutoReview, StringComparison.Ordinal))
            return false;
        if (target.TargetHasActiveReviewAttempt) return false;
        var entered = target.TargetEnteredLaneAt;
        return entered is null || now - entered.Value.ToUniversalTime() >= DefaultStalledThreshold;
    }

    private static string StalledAttentionReason(WaitsOnItem target)
    {
        if (!string.IsNullOrWhiteSpace(target.TargetBlockerReason))
            return $"Waiting on {target.Key}: {target.TargetBlockerReason.Trim()}";
        var since = target.TargetEnteredLaneAt is { } entered ? $" since {entered.ToUniversalTime():u}" : "";
        if (string.Equals(target.TargetState, TaskStates.Escalated, StringComparison.Ordinal))
            return $"Waiting on {target.Key}: escalated{since}, operator decision.";
        return $"Waiting on {target.Key}: no active review attempt{since}.";
    }

    private static IEnumerable<PickupHoldResolution> DependencyDecisions(string? targetKey)
    {
        yield return new PickupHoldResolution(
            PickupHoldResolutionKinds.DropDependency,
            "Drop the dependency",
            "Remove this waits-on edge. The platform never takes this decision automatically.",
            targetKey);
        yield return new PickupHoldResolution(
            PickupHoldResolutionKinds.RepointDependency,
            "Point to a successor card",
            "Replace the edge with the stable key of the card that now owns the prerequisite.",
            targetKey);
        yield return new PickupHoldResolution(
            PickupHoldResolutionKinds.ArchiveWaitingCard,
            "Archive this waiting card",
            "Close the waiting card without changing the prerequisite.",
            targetKey);
    }

    private static PickupHoldStatus Hold(
        string mechanism,
        string reason,
        DateTime since,
        DateTime now,
        bool unsatisfiable,
        params PickupHoldResolution[] resolutions) =>
        HoldClassified(
            mechanism,
            reason,
            since,
            now,
            unsatisfiable,
            unsatisfiable ? PickupHoldClassifications.Unsatisfiable : PickupHoldClassifications.SatisfiableSoon,
            resolutions);

    private static PickupHoldStatus HoldClassified(
        string mechanism,
        string reason,
        DateTime since,
        DateTime now,
        bool unsatisfiable,
        string classification,
        params PickupHoldResolution[] resolutions) =>
        new()
        {
            Mechanism = mechanism,
            Reason = reason,
            SinceUtc = since,
            HeldForSeconds = Age(since, now),
            Unsatisfiable = unsatisfiable,
            Classification = classification,
            Resolutions = [.. resolutions],
        };

    private static long Age(DateTime since, DateTime now)
    {
        if (since == default) return 0;
        var seconds = (long)(now - since.ToUniversalTime()).TotalSeconds;
        return seconds < 0 ? 0 : seconds;
    }

    private static string Describe(string? value, string? fallback = null)
    {
        var text = (value ?? "").Trim();
        if (text.Length > 0) return text;
        var alternative = (fallback ?? "").Trim();
        return alternative.Length > 0 ? alternative : "unknown";
    }
}
