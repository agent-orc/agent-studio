using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2818 - direct matrix over <see cref="PickupHoldPolicy"/>. The policy is
/// the sentence a card in a pickup lane gets to say about itself, so every
/// mechanism the runner admission gate can skip on has a row here, including the
/// precedence between them: the check order mirrors
/// <c>ProjectRunner.IsReadyPickupCandidate</c>, and a card held by two things at
/// once must name the one that actually decided the skip.
/// </summary>
public class PickupHoldPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MonthAgo = Now.AddDays(-34);

    [Fact]
    public void PickupEligibleCard_IsNotHeld()
    {
        Assert.Null(Evaluate(Card()));
    }

    [Fact]
    public void CardOutsideAPickupLane_IsNotHeld()
    {
        // 3-progress has been picked up; "queued but unpickable" is not a state
        // it can be in, and claiming one there would contradict the lane.
        Assert.Null(Evaluate(Card(state: TaskStates.Progress)));
        Assert.Null(Evaluate(Card(state: TaskStates.HumanReview)));
        Assert.Null(Evaluate(Card(state: TaskStates.Backlog)));
    }

    // Case 1 of the reported incident: AGT-2373 waiting on an archived AGT-2372.
    [Fact]
    public void ArchivedReleaseGate_IsADependencyGateHold_ThatCanNeverOpen()
    {
        var hold = Evaluate(Card(), waitsOn: WaitsOn(Item(
            key: "AGT-2372",
            fulfilled: false,
            releaseGate: true,
            waitingForRelease: true,
            unsatisfiable: true,
            unsatisfiableReason: WaitsOnEvaluator.ArchivedGateReason("AGT-2372"))));

        Assert.NotNull(hold);
        Assert.Equal(PickupHoldMechanisms.DependencyGate, hold!.Mechanism);
        Assert.True(hold.Unsatisfiable);
        Assert.Contains("AGT-2372", hold.Reason, StringComparison.Ordinal);
        Assert.Contains("archived", hold.Reason, StringComparison.OrdinalIgnoreCase);
        // The hold is as old as the lane stay, which is the number the operator
        // was missing: a month of standstill read as "queued".
        Assert.Equal(MonthAgo, hold.SinceUtc);
        Assert.Equal(34 * 86400, hold.HeldForSeconds);

        // Exactly the three explicit decisions, offered and never taken automatically.
        Assert.Equal(PickupHoldClassifications.Unsatisfiable, hold.Classification);
        Assert.Equal(
            new[] { PickupHoldResolutionKinds.DropDependency, PickupHoldResolutionKinds.RepointDependency, PickupHoldResolutionKinds.ArchiveWaitingCard },
            hold.Resolutions.Select(resolution => resolution.Kind).ToArray());
        Assert.All(hold.Resolutions, resolution => Assert.Equal("AGT-2372", resolution.TargetKey));
    }

    [Fact]
    public void CompletedButUnreleasedGate_IsAWait_NotAConfigurationError()
    {
        var hold = Evaluate(Card(), waitsOn: WaitsOn(Item(
            key: "LIB-1", fulfilled: false, releaseGate: true, waitingForRelease: true)));

        Assert.NotNull(hold);
        Assert.Equal(PickupHoldMechanisms.DependencyGate, hold!.Mechanism);
        Assert.False(hold.Unsatisfiable);
        Assert.Contains("no explicit release", hold.Reason, StringComparison.Ordinal);
        Assert.Equal(
            new[] { PickupHoldResolutionKinds.ReleaseTarget, PickupHoldResolutionKinds.DropReleaseGate },
            hold.Resolutions.Select(resolution => resolution.Kind).ToArray());
    }

    [Fact]
    public void OpenTarget_NamesTheTargetLane_AndSaysTheGateOpensByItself()
    {
        var hold = Evaluate(Card(), waitsOn: WaitsOn(Item(
            key: "LIB-1", fulfilled: false, targetState: TaskStates.Progress)));

        Assert.NotNull(hold);
        Assert.False(hold!.Unsatisfiable);
        Assert.Equal(PickupHoldClassifications.SatisfiableSoon, hold.Classification);
        Assert.Contains(TaskStates.Progress, hold.Reason, StringComparison.Ordinal);
        Assert.Equal(
            PickupHoldResolutionKinds.AwaitTarget,
            Assert.Single(hold.Resolutions).Kind);
    }

    [Fact]
    public void UnknownTarget_SaysTheKeyDoesNotExist()
    {
        var hold = Evaluate(Card(), waitsOn: WaitsOn(Item(key: "GHOST-9", fulfilled: false, resolved: false)));

        Assert.NotNull(hold);
        Assert.Contains("does not exist", hold!.Reason, StringComparison.Ordinal);
        Assert.Equal(PickupHoldClassifications.Unsatisfiable, hold.Classification);
        Assert.Equal(3, hold.Resolutions.Count);
    }

    [Fact]
    public void EscalatedPrerequisite_IsStalled()
    {
        var hold = Evaluate(Card(), waitsOn: WaitsOn(Item(
            key: "AGT-2736", fulfilled: false, targetState: TaskStates.Escalated)));

        Assert.Equal(PickupHoldClassifications.Stalled, hold!.Classification);
        Assert.Contains("AGT-2736", hold.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoReviewWithoutActiveAttemptPastTimeout_IsStalled()
    {
        var hold = Evaluate(Card(), waitsOn: WaitsOn(Item(
            key: "AGT-2803", fulfilled: false, targetState: TaskStates.AutoReview,
            targetEnteredLaneAt: Now.AddHours(-2), targetHasActiveReviewAttempt: false)));

        Assert.Equal(PickupHoldClassifications.Stalled, hold!.Classification);
    }

    [Fact]
    public void AutoReviewWithActiveAttempt_IsSatisfiableSoon()
    {
        var hold = Evaluate(Card(), waitsOn: WaitsOn(Item(
            key: "AGT-2803", fulfilled: false, targetState: TaskStates.AutoReview,
            targetEnteredLaneAt: Now.AddHours(-2), targetHasActiveReviewAttempt: true)));

        Assert.Equal(PickupHoldClassifications.SatisfiableSoon, hold!.Classification);
    }

    [Fact]
    public void DependencyCycle_OffersExactlyItsThreeEdges_AndKeepsUnrelatedDecisionSeparate()
    {
        var unrelated = Item(
            key: "ARCH-9", fulfilled: false, releaseGate: true,
            waitingForRelease: true, unsatisfiable: true,
            unsatisfiableReason: WaitsOnEvaluator.ArchivedGateReason("ARCH-9"));
        var status = new WaitsOnStatus
        {
            Items = [Item(key: "APP-2", fulfilled: false), unrelated],
            Blocked = true,
            CycleDetected = true,
            CyclePath = ["APP-1", "APP-2", "APP-3", "APP-1"],
            UnsatisfiableGate = true,
        };

        var hold = Evaluate(Card(), waitsOn: status);

        Assert.NotNull(hold);
        Assert.Equal(PickupHoldMechanisms.DependencyGate, hold!.Mechanism);
        Assert.True(hold.Unsatisfiable);
        Assert.Contains("cycle", hold.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            new[]
            {
                PickupHoldResolutionKinds.DropCycleEdge,
                PickupHoldResolutionKinds.DropCycleEdge,
                PickupHoldResolutionKinds.DropCycleEdge,
            },
            hold.Resolutions.Select(resolution => resolution.Kind).ToArray());
        Assert.Equal(
            new[] { "APP-1->APP-2", "APP-2->APP-3", "APP-3->APP-1" },
            hold.Resolutions.Select(resolution => $"{resolution.SourceKey}->{resolution.TargetKey}").ToArray());
        Assert.DoesNotContain(hold.Resolutions, resolution => resolution.TargetKey == "ARCH-9");

        var afterCycleBreak = Evaluate(Card(), waitsOn: status with
        {
            CycleDetected = false,
            CyclePath = [],
        });

        Assert.Equal(PickupHoldClassifications.Unsatisfiable, afterCycleBreak!.Classification);
        Assert.Contains("ARCH-9", afterCycleBreak.Reason, StringComparison.Ordinal);
        Assert.All(afterCycleBreak.Resolutions, resolution => Assert.Equal("ARCH-9", resolution.TargetKey));
    }

    [Fact]
    public void FulfilledDependencies_DoNotHoldTheCard()
    {
        var status = new WaitsOnStatus { Items = [Item(key: "LIB-1", fulfilled: true)], Blocked = false };

        Assert.Null(Evaluate(Card(), waitsOn: status));
    }

    // Case 2 of the reported incident: AGT-2738 refused by agent-runner-01.
    [Fact]
    public void DispatchRejection_NamesRunnerCodeReasonAndWhen()
    {
        var rejectedAt = new DateTime(2026, 9, 6, 19, 47, 45, DateTimeKind.Utc);
        var hold = Evaluate(Card(), rejection: new RemoteDispatchRejection
        {
            Code = "capability-mismatch",
            RunnerId = "agent-runner-01",
            RunnerName = "agent-runner-01",
            Reason = "Required capability 'task-server:connectivity' is advertised as unavailable.",
            RejectedAtUtc = rejectedAt,
        });

        Assert.NotNull(hold);
        Assert.Equal(PickupHoldMechanisms.DispatchRejection, hold!.Mechanism);
        Assert.False(hold.Unsatisfiable);
        Assert.Contains("agent-runner-01", hold.Reason, StringComparison.Ordinal);
        Assert.Contains("capability-mismatch", hold.Reason, StringComparison.Ordinal);
        Assert.Contains("task-server:connectivity", hold.Reason, StringComparison.Ordinal);
        // The hold is dated from the refusal, not from the lane entry: that is
        // the moment the card stopped being pickable.
        Assert.Equal(rejectedAt, hold.SinceUtc);
        Assert.Equal(
            new[] { PickupHoldResolutionKinds.RestoreRunnerCapability, PickupHoldResolutionKinds.RetryDispatch },
            hold.Resolutions.Select(resolution => resolution.Kind).ToArray());
    }

    [Fact]
    public void DependencyGate_OutranksAStaleDispatchRejection()
    {
        // A card the local gate already refuses was never offered to a runner,
        // so a leftover refusal must not claim to be what is holding it.
        var hold = Evaluate(
            Card(),
            waitsOn: WaitsOn(Item(key: "LIB-1", fulfilled: false, targetState: TaskStates.Ready)),
            rejection: new RemoteDispatchRejection { Code = "capability-mismatch", RunnerName = "r1" });

        Assert.Equal(PickupHoldMechanisms.DependencyGate, hold!.Mechanism);
    }

    [Fact]
    public void EpicInAPickupLane_IsAContainerHold()
    {
        var hold = Evaluate(Card(kind: TaskKinds.Epic));

        Assert.Equal(PickupHoldMechanisms.EpicContainer, hold!.Mechanism);
        Assert.Equal(PickupHoldResolutionKinds.DecomposeEpic, Assert.Single(hold.Resolutions).Kind);
    }

    [Fact]
    public void HumanAgent_IsAPickupPolicyHold()
    {
        var hold = Evaluate(Card(agent: AgentTypes.Human));

        Assert.Equal(PickupHoldMechanisms.PickupPolicy, hold!.Mechanism);
        Assert.Contains("human", hold.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PickupHoldResolutionKinds.AssignAgent, Assert.Single(hold.Resolutions).Kind);
    }

    [Fact]
    public void HumanDecisionMarker_IsAPickupPolicyHold()
    {
        var hold = Evaluate(Card(id: TaskSlugs.HumanDecisionNeededPrefix + "pick-a-column"));

        Assert.Equal(PickupHoldMechanisms.PickupPolicy, hold!.Mechanism);
        Assert.Equal(PickupHoldResolutionKinds.DecideEscalation, Assert.Single(hold.Resolutions).Kind);
    }

    [Fact]
    public void FixtureCard_IsAPickupPolicyHold_WithNoWayOutToOffer()
    {
        var hold = Evaluate(Card(fixture: true));

        Assert.Equal(PickupHoldMechanisms.PickupPolicy, hold!.Mechanism);
        Assert.Empty(hold.Resolutions);
    }

    [Fact]
    public void IntakeEnabledAndNotPassed_IsAPickupPolicyHold()
    {
        var hold = Evaluate(Card(phase: "intake-blocked"), intakeEnabled: true);

        Assert.Equal(PickupHoldMechanisms.PickupPolicy, hold!.Mechanism);
        Assert.Contains("intake", hold.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("intake-blocked", hold.Reason, StringComparison.Ordinal);
        Assert.Equal(PickupHoldResolutionKinds.PassIntake, Assert.Single(hold.Resolutions).Kind);
    }

    [Fact]
    public void IntakePassed_DoesNotHoldTheCard()
    {
        Assert.Null(Evaluate(Card(phase: LifecyclePhases.IntakePassed), intakeEnabled: true));
    }

    [Fact]
    public void IntakeDisabled_DoesNotHoldAnUnpassedCard()
    {
        Assert.Null(Evaluate(Card(phase: "human-ready"), intakeEnabled: false));
    }

    [Fact]
    public void ArmedCrashBackoff_IsACooldownHold_ThatExpiresByItself()
    {
        var hold = Evaluate(Card(), crashBackoffUntil: Now.AddMinutes(4));

        Assert.Equal(PickupHoldMechanisms.CrashBackoff, hold!.Mechanism);
        Assert.False(hold.Unsatisfiable);
        Assert.Equal(PickupHoldResolutionKinds.WaitOutBackoff, Assert.Single(hold.Resolutions).Kind);
    }

    [Fact]
    public void ExpiredCrashBackoff_DoesNotHoldTheCard()
    {
        Assert.Null(Evaluate(Card(), crashBackoffUntil: Now.AddMinutes(-1)));
    }

    [Fact]
    public void HoldAge_FallsBackToCreationWhenTheLaneStampIsMissing()
    {
        // Legacy cards that predate enteredLaneAt must still age visibly rather
        // than reporting a hold that started at the Unix epoch.
        var card = Card(enteredLaneAt: DateTime.MinValue) with { CreatedAt = Now.AddDays(-3) };

        var hold = Evaluate(card, waitsOn: WaitsOn(Item(key: "LIB-1", fulfilled: false)));

        Assert.Equal(3 * 86400, hold!.HeldForSeconds);
    }

    private static PickupHoldStatus? Evaluate(
        TaskInfo card,
        WaitsOnStatus? waitsOn = null,
        bool intakeEnabled = false,
        DateTime? crashBackoffUntil = null,
        RemoteDispatchRejection? rejection = null)
        => PickupHoldPolicy.Evaluate(new PickupHoldFacts(
            card, waitsOn, intakeEnabled, crashBackoffUntil, rejection, Now));

    private static TaskInfo Card(
        string id = "consumer",
        string state = TaskStates.Ready,
        string agent = AgentTypes.Claude,
        string kind = TaskKinds.Task,
        string? phase = null,
        bool fixture = false,
        DateTime? enteredLaneAt = null) => new()
    {
        Id = id,
        Key = "APP-1",
        Title = "Consumer",
        State = state,
        Agent = agent,
        Kind = kind,
        Phase = phase,
        Fixture = fixture,
        ProjectName = "app",
        WatchPath = "/ws/app",
        EnteredLaneAt = enteredLaneAt ?? MonthAgo,
        CreatedAt = MonthAgo,
    };

    private static WaitsOnStatus WaitsOn(WaitsOnItem item) => new()
    {
        Items = [item],
        Blocked = !item.Fulfilled,
        UnsatisfiableGate = item.Unsatisfiable,
    };

    private static WaitsOnItem Item(
        string key,
        bool fulfilled,
        bool resolved = true,
        bool releaseGate = false,
        bool waitingForRelease = false,
        bool unsatisfiable = false,
        string unsatisfiableReason = "",
        string? targetState = null,
        DateTime? targetEnteredLaneAt = null,
        bool targetHasActiveReviewAttempt = false) => new()
    {
        Key = key,
        Resolved = resolved,
        Fulfilled = fulfilled,
        ReleaseGate = releaseGate,
        WaitingForRelease = waitingForRelease,
        Unsatisfiable = unsatisfiable,
        UnsatisfiableReason = unsatisfiableReason,
        TargetState = targetState,
        TargetEnteredLaneAt = targetEnteredLaneAt,
        TargetHasActiveReviewAttempt = targetHasActiveReviewAttempt,
    };
}
