using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts.BatchGate;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>AGT-3568: snapshot, close and admission decisions of the formation planner.</summary>
public sealed class BatchFormationPlannerDecisionTests
{
    [Fact]
    public void Snapshot_keeps_only_members_no_scope_rule_excludes_in_order()
        => Assert.Equal(["A", "C"], BatchFormationPolicy.SnapshotEligible(
            [("A", false), ("B", true), ("C", false)]));

    [Fact]
    public void Recorded_plan_names_excluded_members_and_never_proposes_them()
    {
        var plan = new BatchPlanDecision("close", "ready", null, ["A", "B"], 2, []);

        var recorded = BatchFormationPolicy.WithExcluded(plan, [new BatchPlanExcludedMember("B", "MissingResultSha")]);

        Assert.Equal(["A"], recorded.ProposedMembers);
        Assert.Equal("B", Assert.Single(recorded.ExcludedMembers!).TaskKey);
    }

    [Fact]
    public void Close_takes_eligible_proposals_in_order_without_duplicates_up_to_the_maximum()
    {
        var selection = BatchFormationPolicy.CloseSelection(["A", "B", "C", "D"], ["C", "X", "A", "C", "B", "D"],
            minimumSize: 2, maximumSize: 3);

        Assert.Equal(["C", "A", "B"], selection.SelectedKeys);
        Assert.False(selection.BelowMinimum);
    }

    [Fact]
    public void Close_below_the_minimum_size_forms_nothing()
        => Assert.True(BatchFormationPolicy.CloseSelection(["A"], ["A", "B"], 2, 8).BelowMinimum);

    [Fact]
    public void Look_ahead_close_proposes_proven_green_members_first()
        => Assert.Equal(["G", "A"], BatchFormationPolicy.LookAheadCloseKeys(["G"], ["A", "G"]));

    [Theory]
    [InlineData("close", BatchFormationRoute.Close, true)]
    [InlineData("per-task", BatchFormationRoute.PerTask, true)]
    [InlineData("wait", BatchFormationRoute.Wait, false)]
    [InlineData("idle", BatchFormationRoute.Wait, false)]
    public void Look_ahead_route_and_close_now(string decision, BatchFormationRoute route, bool consumesCloseNow)
    {
        Assert.Equal(route, BatchFormationPolicy.LookAheadRoute(decision));
        Assert.Equal(consumesCloseNow, BatchFormationPolicy.ConsumesCloseNow(route));
    }

    [Theory]
    [InlineData(true, false, null, 0, 2, 2, "close", "threshold reached")]
    [InlineData(false, true, null, 0, 1, 1, "per-task", "per-task gate selected")]
    [InlineData(false, true, "lone card", 0, 1, 1, "per-task", "lone card")]
    [InlineData(false, false, null, 0, 0, 0, "idle", "waiting for threshold or deadline")]
    [InlineData(false, false, null, 0, 1, 1, "wait", "waiting for threshold or deadline")]
    public void Threshold_plan_names_its_decision(bool closed, bool perTask, string? reason, int partners,
        int eligible, int current, string decision, string expectedReason)
        => Assert.Equal(new BatchThresholdPlan(decision, expectedReason),
            BatchFormationPolicy.ThresholdPlan(closed, perTask, reason, partners, eligible, current));

    [Fact]
    public void Threshold_plan_waits_for_queued_pre_check_partners()
        => Assert.Equal(new BatchThresholdPlan("wait", BatchGateLookAheadPlanner.QueuedPartnersReason),
            BatchFormationPolicy.ThresholdPlan(false, false, null, 1, 0, 0));

    [Theory]
    [InlineData(true, true, true, BatchAdmissionVerdict.SupersededGeneration)]
    [InlineData(false, true, true, BatchAdmissionVerdict.PolicyExcluded)]
    [InlineData(false, false, true, BatchAdmissionVerdict.OperatorEject)]
    [InlineData(false, false, false, BatchAdmissionVerdict.Admit)]
    public void Admission_checks_a_member_in_order(bool superseded, bool excluded, bool eject,
        BatchAdmissionVerdict expected)
        => Assert.Equal(expected, BatchFormationPolicy.AdmitMember(superseded, excluded, eject));

    [Theory]
    [InlineData(1, true, true, true, false, BatchAdmissionReplay.Conflict)]
    [InlineData(0, true, true, true, false, BatchAdmissionReplay.Admit)]
    [InlineData(0, true, false, true, false, BatchAdmissionReplay.EjectUnattributable)]
    [InlineData(0, true, true, false, false, BatchAdmissionReplay.EjectUnattributable)]
    [InlineData(0, false, false, false, true, BatchAdmissionReplay.EjectUnattributable)]
    [InlineData(0, false, false, false, false, BatchAdmissionReplay.Pause)]
    public void Admission_classifies_a_member_replay(int conflicts, bool success, bool tip, bool mapped,
        bool unattributable, BatchAdmissionReplay expected)
        => Assert.Equal(expected, BatchFormationPolicy.AdmitReplay(conflicts, success, tip, mapped, unattributable));

    [Theory]
    [InlineData(true, true, 1, 0, 2, BatchAdmissionOutcome.BaseMoved)]
    [InlineData(false, true, 1, 3, 2, BatchAdmissionOutcome.ReplacementChanged)]
    [InlineData(false, true, 0, 3, 2, BatchAdmissionOutcome.Unchanged)]
    [InlineData(false, false, 2, 1, 2, BatchAdmissionOutcome.BelowMinimum)]
    [InlineData(false, false, 1, 2, 2, BatchAdmissionOutcome.Trimmed)]
    [InlineData(false, false, 0, 2, 2, BatchAdmissionOutcome.Unchanged)]
    public void Admission_outcome_prefers_a_moved_base_then_a_changed_replacement_then_the_minimum(
        bool baseMoved, bool replacement, int rejected, int admitted, int minimum, BatchAdmissionOutcome expected)
        => Assert.Equal(expected, BatchFormationPolicy.AdmissionOutcome(baseMoved, replacement, rejected, admitted,
            minimum));
}
